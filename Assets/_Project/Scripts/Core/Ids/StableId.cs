using System;

namespace Skyrift.Core.Ids
{
    /// <summary>
    /// Immutable content identifier (TECH_DOC §4.0/§5). Every cross-asset reference and every asset
    /// address uses a StableId, never a direct object reference, so content can be lazy-loaded and
    /// validated. Format: ^[a-z0-9_]{3,48}$ — validated by hand, no Regex allocation.
    /// </summary>
    public readonly struct StableId : IEquatable<StableId>
    {
        public const int MinLength = 3;
        public const int MaxLength = 48;

        public static readonly StableId None = new StableId(string.Empty);

        public string Value { get; }

        private StableId(string value)
        {
            Value = value ?? string.Empty;
        }

        public bool IsValid => IsValidString(Value);
        public bool IsNone => string.IsNullOrEmpty(Value);

        /// <summary>Validation used by catalog checks and editor tooling. Never called per frame.</summary>
        public static bool IsValidString(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.Length < MinLength || value.Length > MaxLength) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }

            return true;
        }

        /// <summary>Wraps an already-validated id. Throws on invalid input — use at authoring/boot time only.</summary>
        public static StableId Create(string value)
        {
            if (!IsValidString(value))
                throw new ArgumentException($"'{value}' is not a valid StableId (^[a-z0-9_]{{{MinLength},{MaxLength}}}$).", nameof(value));
            return new StableId(value);
        }

        public static bool TryCreate(string value, out StableId id)
        {
            if (IsValidString(value))
            {
                id = new StableId(value);
                return true;
            }

            id = None;
            return false;
        }

        /// <summary>Implicit conversion keeps data authoring terse; invalid strings become None and fail validation.</summary>
        public static implicit operator StableId(string value) => new StableId(value);
        public static implicit operator string(StableId id) => id.Value;

        public bool Equals(StableId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is StableId other && Equals(other);
        public override int GetHashCode() => Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value ?? string.Empty;

        public static bool operator ==(StableId a, StableId b) => a.Equals(b);
        public static bool operator !=(StableId a, StableId b) => !a.Equals(b);
    }
}
