using System;

namespace Skyrift.Core.Randomization
{
    /// <summary>
    /// Seeded, platform-independent RNG (TECH_DOC §4.0). Determinism contract: same seed + same catalogs
    /// + same Remote Config values produces the same run (ADR-0005). Never use UnityEngine.Random in gameplay.
    /// </summary>
    public interface IRandomProvider
    {
        uint NextUInt();
        int Range(int minInclusive, int maxExclusive);
        float Value01();
        IRandomProvider Fork(string streamName);
    }

    /// <summary>
    /// xorshift128. Integer-only arithmetic, identical on every IL2CPP/Mono target, no allocation.
    /// </summary>
    public sealed class XorshiftRandom : IRandomProvider
    {
        private uint _x, _y, _z, _w;

        public XorshiftRandom(uint seed)
        {
            // A zero state would lock the generator at zero; force a non-zero first word.
            _x = seed == 0u ? 0x9E3779B9u : seed;
            _y = 362436069u;
            _z = 521288629u;
            _w = 88675123u;
            // Discard the first few outputs so low-entropy seeds decorrelate.
            for (int i = 0; i < 8; i++) NextUInt();
        }

        public uint NextUInt()
        {
            uint t = _x ^ (_x << 11);
            _x = _y;
            _y = _z;
            _z = _w;
            _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
            return _w;
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % span);
        }

        /// <summary>Uniform in [0,1) with 24 bits of resolution — enough for weighted picks.</summary>
        public float Value01()
        {
            return (NextUInt() & 0xFFFFFFu) / (float)0x1000000u;
        }

        /// <summary>Independent stream derived from this one, so subsystems do not consume each other's draws.</summary>
        public IRandomProvider Fork(string streamName)
        {
            uint hash = Hash(streamName);
            return new XorshiftRandom(hash ^ NextUInt());
        }

        /// <summary>FNV-1a over the stream name. Allocation-free, stable across platforms.</summary>
        public static uint Hash(string value)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            if (string.IsNullOrEmpty(value)) return hash;

            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= prime;
            }

            return hash;
        }
    }
}
