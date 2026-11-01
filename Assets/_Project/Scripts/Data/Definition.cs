using System.Collections.Generic;
using Skyrift.Core.Ids;
using UnityEngine;

namespace Skyrift.Data
{
    /// <summary>Every content definition exposes a stable, immutable id (TECH_DOC §5).</summary>
    public interface IIdentifiable
    {
        StableId Id { get; }
    }

    /// <summary>
    /// Authoring-time validation. Rules are listed in TECH_DOC §5.14 and enforced by the editor build
    /// script (blocking) and at boot in DEV (report + drop).
    /// </summary>
    public interface IValidatable
    {
        void Validate(ValidationReport report);
    }

    /// <summary>One validation finding. Severity decides whether a PROD build may ship.</summary>
    public readonly struct ValidationIssue
    {
        public readonly bool IsError;
        public readonly string Type;
        public readonly string Id;
        public readonly string Rule;
        public readonly string Message;

        public ValidationIssue(bool isError, string type, string id, string rule, string message)
        {
            IsError = isError;
            Type = type;
            Id = id;
            Rule = rule;
            Message = message;
        }

        public override string ToString() => $"{(IsError ? "ERROR" : "WARN")} {Type}/{Id} [{Rule}] {Message}";
    }

    /// <summary>Collects findings. Allocation is fine: validation runs in the editor and at boot, never per frame.</summary>
    public sealed class ValidationReport
    {
        private readonly List<ValidationIssue> _issues = new List<ValidationIssue>(32);

        public IReadOnlyList<ValidationIssue> Issues => _issues;
        public int ErrorCount { get; private set; }
        public bool IsValid => ErrorCount == 0;

        public void Error(string type, string id, string rule, string message)
        {
            ErrorCount++;
            _issues.Add(new ValidationIssue(true, type, id, rule, message));
        }

        public void Warn(string type, string id, string rule, string message)
        {
            _issues.Add(new ValidationIssue(false, type, id, rule, message));
        }
    }

    /// <summary>
    /// Base class for content ScriptableObjects. Gives every definition an id field, a display-name
    /// localization key and a validation entry point, so new content types only add their own fields.
    /// </summary>
    public abstract class DefinitionBase : ScriptableObject, IIdentifiable, IValidatable
    {
        [SerializeField]
        [Tooltip("Immutable snake_case id. Never rename a shipped id: saves and analytics reference it.")]
        private string id;

        [SerializeField]
        [Tooltip("Localization key for the display name. No user-facing strings live in data.")]
        private string displayNameKey;

        public StableId Id => id;
        public string DisplayNameKey => displayNameKey;

        public virtual void Validate(ValidationReport report)
        {
            string typeName = GetType().Name;

            if (!StableId.IsValidString(id))
                report.Error(typeName, id, "id_format", "id must match ^[a-z0-9_]{3,48}$.");

            if (string.IsNullOrEmpty(displayNameKey))
                report.Warn(typeName, id, "loc_key", "displayNameKey is empty.");
        }

#if UNITY_EDITOR
        /// <summary>Keeps the asset file name and the id in sync so addresses stay predictable.</summary>
        protected virtual void OnValidate()
        {
            if (string.IsNullOrEmpty(id)) return;
            id = id.Trim().ToLowerInvariant();
        }
#endif
    }
}
