using System.Collections.Generic;
using Skyrift.Core.Events;
using Skyrift.Data;
using UnityEngine;

namespace Skyrift.Gameplay
{
    /// <summary>Everything a score contribution or modifier may read (TECH_DOC §4.11). Readonly, passed in.</summary>
    public readonly struct ScoreContext
    {
        public readonly float DistanceMetres;
        public readonly float PlayerSpeed;
        public readonly int Combo;
        public readonly float ElapsedSeconds;
        public readonly float Multiplier;

        public ScoreContext(float distanceMetres, float playerSpeed, int combo, float elapsedSeconds, float multiplier)
        {
            DistanceMetres = distanceMetres;
            PlayerSpeed = playerSpeed;
            Combo = combo;
            ElapsedSeconds = elapsedSeconds;
            Multiplier = multiplier;
        }
    }

    /// <summary>
    /// Immutable score composition (TECH_DOC §4.11). The results screen reads this and never recomputes,
    /// so a balance change between the run and the results screen cannot alter what the player earned.
    /// </summary>
    public readonly struct ScoreBreakdown
    {
        public readonly float DistanceMetres;
        public readonly long DistanceScore;
        public readonly long CollectionScore;
        public readonly long ComboBonus;
        public readonly long ContributorScore;
        public readonly float Multiplier;
        public readonly long Total;
        public readonly int Coins;
        public readonly int Keys;
        public readonly int Gems;
        public readonly int BestCombo;

        public ScoreBreakdown(float distanceMetres, long distanceScore, long collectionScore, long comboBonus,
            long contributorScore, float multiplier, long total, int coins, int keys, int gems, int bestCombo)
        {
            DistanceMetres = distanceMetres;
            DistanceScore = distanceScore;
            CollectionScore = collectionScore;
            ComboBonus = comboBonus;
            ContributorScore = contributorScore;
            Multiplier = multiplier;
            Total = total;
            Coins = coins;
            Keys = keys;
            Gems = gems;
            BestCombo = bestCombo;
        }
    }

    /// <summary>Additive score source (TECH_DOC §4.11 extension point) — e.g. a near-miss bonus in M2.</summary>
    public interface IScoreContributor
    {
        float Contribute(in ScoreContext ctx);
    }

    /// <summary>Multiplicative score shaping applied after contributions (TECH_DOC §4.11 extension point).</summary>
    public interface IScoreModifier
    {
        float Modify(float baseScore, in ScoreContext ctx);
    }

    /// <summary>
    /// One deterministic score derived from contributions and multipliers (TECH_DOC §4.11).
    /// Weights, the combo window and the mission-set step are all data; nothing here is a balance number.
    /// </summary>
    public interface IScoreCalculator
    {
        long CurrentScore { get; }
        float DistanceMetres { get; }
        int Combo { get; }
        float Multiplier { get; }

        /// <summary>Allocation-free. Called once per simulated frame by the run controller.</summary>
        void Tick(float dt, float playerSpeed);

        void OnCollected(int amount, CollectibleKind kind);
        void OnPowerUpMultiplierChanged(float factor);

        /// <summary>Completed mission sets raise the global multiplier (M4 feeds this; M1 passes 0).</summary>
        void SetMissionSetsCompleted(int sets);

        /// <summary>Stumble and crash break the chain, as specified in §4.11.</summary>
        void BreakCombo();

        ScoreBreakdown Breakdown { get; }
        void ResetForRun();

        void AddContributor(IScoreContributor contributor);
        void RemoveContributor(IScoreContributor contributor);
        void AddModifier(IScoreModifier modifier);
        void RemoveModifier(IScoreModifier modifier);
    }

    /// <inheritdoc cref="IScoreCalculator"/>
    public sealed class ScoreCalculator : IScoreCalculator
    {
        private readonly List<IScoreContributor> _contributors = new List<IScoreContributor>(4);
        private readonly List<IScoreModifier> _modifiers = new List<IScoreModifier>(4);

        private readonly ScoringConfig _config;
        private readonly IEventBus _bus;

        private float _distance;
        private float _elapsed;
        private float _speed;

        private long _collectionScore;
        private long _comboBonus;
        private long _contributorScore;
        private int _combo;
        private int _bestCombo;
        private float _comboTimer;

        private int _coins;
        private int _keys;
        private int _gems;

        private float _powerUpMultiplier = 1f;
        private int _missionSets;

        private long _total;
        private long _lastPublished;
        private float _lastPublishedMultiplier = -1f;
        private int _lastPublishedCombo = -1;

        public long CurrentScore => _total;
        public float DistanceMetres => _distance;
        public int Combo => _combo;
        public float Multiplier => ComputeMultiplier();

        public ScoreCalculator(ScoringConfig config, IEventBus bus)
        {
            _config = config;
            _bus = bus;
        }

        public ScoreBreakdown Breakdown => new ScoreBreakdown(
            _distance,
            (long)(_distance * (_config != null ? _config.distanceWeight : 0f)),
            _collectionScore,
            _comboBonus,
            _contributorScore,
            ComputeMultiplier(),
            _total,
            _coins,
            _keys,
            _gems,
            _bestCombo);

        public void ResetForRun()
        {
            _distance = 0f;
            _elapsed = 0f;
            _speed = 0f;
            _collectionScore = 0L;
            _comboBonus = 0L;
            _contributorScore = 0L;
            _combo = 0;
            _bestCombo = 0;
            _comboTimer = 0f;
            _coins = 0;
            _keys = 0;
            _gems = 0;
            _powerUpMultiplier = 1f;
            _missionSets = 0;
            _total = 0L;
            _lastPublished = -1L;
            _lastPublishedMultiplier = -1f;
            _lastPublishedCombo = -1;

            _contributors.Clear();
            _modifiers.Clear();

            PublishAll();
        }

        public void Tick(float dt, float playerSpeed)
        {
            if (dt <= 0f || _config == null) return;

            _elapsed += dt;
            _speed = playerSpeed;
            _distance += playerSpeed * dt;

            if (_comboTimer > 0f)
            {
                _comboTimer -= dt;
                if (_comboTimer <= 0f && _combo != 0)
                {
                    _combo = 0;
                    PublishCombo();
                }
            }

            Recompute();
        }

        public void OnCollected(int amount, CollectibleKind kind)
        {
            if (amount <= 0 || _config == null) return;

            switch (kind)
            {
                case CollectibleKind.Coin: _coins += amount; break;
                case CollectibleKind.Key: _keys += amount; break;
                case CollectibleKind.Gem: _gems += amount; break;
            }

            _collectionScore += (long)(amount * _config.WeightFor(kind));

            // A collection inside the window extends the chain; the first one after a gap starts it at 1.
            _combo = _comboTimer > 0f ? Mathf.Min(_config.comboMax, _combo + 1) : 1;
            _comboTimer = _config.comboWindow;
            if (_combo > _bestCombo) _bestCombo = _combo;

            _comboBonus += (long)(_config.comboBonusPerStep * _combo);

            PublishCombo();
            Recompute();
        }

        public void OnPowerUpMultiplierChanged(float factor)
        {
            _powerUpMultiplier = factor > 0f ? factor : 1f;
            Recompute();
        }

        public void SetMissionSetsCompleted(int sets)
        {
            _missionSets = sets > 0 ? sets : 0;
            Recompute();
        }

        public void BreakCombo()
        {
            _comboTimer = 0f;
            if (_combo == 0) return;
            _combo = 0;
            PublishCombo();
        }

        public void AddContributor(IScoreContributor contributor)
        {
            if (contributor != null && !_contributors.Contains(contributor)) _contributors.Add(contributor);
        }

        public void RemoveContributor(IScoreContributor contributor)
        {
            if (contributor != null) _contributors.Remove(contributor);
        }

        public void AddModifier(IScoreModifier modifier)
        {
            if (modifier != null && !_modifiers.Contains(modifier)) _modifiers.Add(modifier);
        }

        public void RemoveModifier(IScoreModifier modifier)
        {
            if (modifier != null) _modifiers.Remove(modifier);
        }

        private float ComputeMultiplier()
        {
            float step = _config != null ? _config.missionSetMultiplierStep : 0f;
            return Mathf.Max(0f, _powerUpMultiplier * (1f + (_missionSets * step)));
        }

        private void Recompute()
        {
            if (_config == null) return;

            float multiplier = ComputeMultiplier();
            var ctx = new ScoreContext(_distance, _speed, _combo, _elapsed, multiplier);

            float baseScore = (_distance * _config.distanceWeight) + _collectionScore + _comboBonus;

            float contributed = 0f;
            for (int i = 0; i < _contributors.Count; i++) contributed += _contributors[i].Contribute(in ctx);
            baseScore += contributed;

            // Kept as its own line item so the results screen can show what a bonus source earned. It is a
            // snapshot of the contributors' current total, exactly like DistanceScore — never re-derived.
            _contributorScore = (long)Mathf.Max(0f, contributed);

            for (int i = 0; i < _modifiers.Count; i++) baseScore = _modifiers[i].Modify(baseScore, in ctx);

            // Multipliers apply to the whole composition, exactly once, so two sources cannot compound
            // differently depending on the order they were registered in.
            long total = (long)Mathf.Max(0f, baseScore * multiplier);
            if (total == _total)
            {
                PublishMultiplier(multiplier);
                return;
            }

            long delta = total - _total;
            _total = total;

            _bus?.Publish(new ScoreChangedEvent(_total, delta));
            PublishMultiplier(multiplier);
        }

        private void PublishAll()
        {
            _bus?.Publish(new ScoreChangedEvent(_total, _total));
            PublishCombo();
            PublishMultiplier(ComputeMultiplier());
        }

        private void PublishCombo()
        {
            if (_combo == _lastPublishedCombo) return;
            _lastPublishedCombo = _combo;
            _bus?.Publish(new ComboChangedEvent(_combo));
        }

        private void PublishMultiplier(float multiplier)
        {
            if (Mathf.Approximately(multiplier, _lastPublishedMultiplier)) return;
            _lastPublishedMultiplier = multiplier;
            _bus?.Publish(new ScoreMultiplierChangedEvent(multiplier));
        }
    }
}