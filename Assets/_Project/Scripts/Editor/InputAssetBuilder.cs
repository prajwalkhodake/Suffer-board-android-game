using Skyrift.Controls;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyrift.Editor
{
    /// <summary>
    /// Authors the InputActionAsset whose map and action names are the contract in
    /// <see cref="InputActionNames"/> (TECH_DOC §4.3). Both input sources resolve actions by those names, so
    /// this file and that class are the two halves of one agreement: renaming an action here without
    /// renaming the constant produces a silently dead input path, which is why the validator checks the
    /// asset against the constants rather than trusting the author.
    ///
    /// One shared "Player" map serves touch and keyboard. That is deliberate — the bootstrap activates
    /// exactly one source and deactivates the other, because two live sources on one map would each disable
    /// the map the other needs.
    /// </summary>
    public static class InputAssetBuilder
    {
        public const string AssetName = "SkyriftInput";

        /// <summary>Asset path of the generated action asset.</summary>
        public static string AssetPath => SkyriftPaths.SettingsInput + "/" + AssetName + ".inputactions";

        /// <summary>
        /// Builds the asset, or returns the existing one. Actions are only added when missing, so a binding
        /// a developer tuned by hand (a gamepad deadzone, an extra key) survives a re-run.
        /// </summary>
        public static InputActionAsset Build()
        {
            SkyriftPaths.EnsureFolder(SkyriftPaths.SettingsInput);

            InputActionAsset asset = AssetDatabase.LoadAssetAtPath<InputActionAsset>(AssetPath);
            bool created = asset == null;
            if (created)
            {
                asset = ScriptableObject.CreateInstance<InputActionAsset>();
                asset.name = AssetName;
            }

            InputActionMap map = asset.FindActionMap(InputActionNames.Map, throwIfNotFound: false);
            if (map == null) map = asset.AddActionMap(InputActionNames.Map);

            AddPoint(map);
            AddPress(map);
            AddButton(map, InputActionNames.Left, "<Keyboard>/a", "<Keyboard>/leftArrow", "<Gamepad>/dpad/left");
            AddButton(map, InputActionNames.Right, "<Keyboard>/d", "<Keyboard>/rightArrow", "<Gamepad>/dpad/right");
            AddButton(map, InputActionNames.Up, "<Keyboard>/w", "<Keyboard>/upArrow", "<Gamepad>/dpad/up");
            AddButton(map, InputActionNames.Down, "<Keyboard>/s", "<Keyboard>/downArrow", "<Gamepad>/dpad/down");
            AddButton(map, InputActionNames.Tap, "<Keyboard>/space", "<Gamepad>/buttonSouth");
            AddButton(map, InputActionNames.Pause, "<Keyboard>/escape", "<Keyboard>/p", "<Gamepad>/start");

            if (created) AssetDatabase.CreateAsset(asset, AssetPath);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        /// <summary>
        /// Pointer position as a Vector2 value. Both a Pointer path and an explicit touchscreen path are
        /// bound: Touchscreen is not a Pointer subclass in every Input System version, and binding only the
        /// abstract path is the classic way touch position silently reads as zero on a device while working
        /// perfectly with a mouse in the editor.
        /// </summary>
        private static void AddPoint(InputActionMap map)
        {
            InputAction action = EnsureAction(map, InputActionNames.Point, InputActionType.Value, "Vector2");
            EnsureBinding(action, "<Pointer>/position");
            EnsureBinding(action, "<Touchscreen>/primaryTouch/position");
        }

        /// <summary>
        /// Press/release. A Button action with no interaction fires <c>performed</c> on press and
        /// <c>canceled</c> on release, which is exactly the pair <c>TouchInputSource</c> hooks to time a
        /// gesture. Adding a Hold or Tap interaction here would break that timing.
        /// </summary>
        private static void AddPress(InputActionMap map)
        {
            InputAction action = EnsureAction(map, InputActionNames.Press, InputActionType.Button, "Button");
            EnsureBinding(action, "<Pointer>/press");
            EnsureBinding(action, "<Touchscreen>/primaryTouch/press");
        }

        private static void AddButton(InputActionMap map, string name, params string[] paths)
        {
            InputAction action = EnsureAction(map, name, InputActionType.Button, "Button");
            for (int i = 0; i < paths.Length; i++) EnsureBinding(action, paths[i]);
        }

        private static InputAction EnsureAction(InputActionMap map, string name, InputActionType type,
            string expectedControlType)
        {
            InputAction existing = map.FindAction(name, throwIfNotFound: false);
            if (existing != null) return existing;

            return map.AddAction(name, type, expectedControlType: expectedControlType);
        }

        /// <summary>
        /// Adds a binding only when that exact path is not already present, so re-running the tool cannot
        /// accumulate duplicate bindings — a duplicated button binding fires the callback twice per press,
        /// which would read as a double lane change.
        /// </summary>
        private static void EnsureBinding(InputAction action, string path)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                if (string.Equals(action.bindings[i].path, path, System.StringComparison.Ordinal)) return;
            }

            action.AddBinding(path);
        }

        /// <summary>
        /// Authoring-time check that the asset really does expose every contracted action with a usable
        /// binding. Reported as an error, not a warning: an action with no binding is a dead control that
        /// no amount of runtime logging would explain.
        /// </summary>
        public static void Validate(InputActionAsset asset, ValidationReport report)
        {
            const string type = nameof(InputActionAsset);

            if (asset == null)
            {
                report.Error(type, AssetName, "asset", "No InputActionAsset assigned. Run Tools > Skyrift > Build M1 Greybox.");
                return;
            }

            InputActionMap map = asset.FindActionMap(InputActionNames.Map, throwIfNotFound: false);
            if (map == null)
            {
                report.Error(type, AssetName, "map", $"Action map '{InputActionNames.Map}' is missing.");
                return;
            }

            ValidateAction(map, InputActionNames.Point, 2, report);
            ValidateAction(map, InputActionNames.Press, 2, report);
            ValidateAction(map, InputActionNames.Left, 1, report);
            ValidateAction(map, InputActionNames.Right, 1, report);
            ValidateAction(map, InputActionNames.Up, 1, report);
            ValidateAction(map, InputActionNames.Down, 1, report);
            ValidateAction(map, InputActionNames.Tap, 1, report);
            ValidateAction(map, InputActionNames.Pause, 1, report);
        }

        private static void ValidateAction(InputActionMap map, string name, int minimumBindings, ValidationReport report)
        {
            InputAction action = map.FindAction(name, throwIfNotFound: false);
            if (action == null)
            {
                report.Error(nameof(InputActionAsset), AssetName, "action", $"Action '{name}' is missing from map '{map.name}'.");
                return;
            }

            if (action.bindings.Count < minimumBindings)
            {
                report.Error(nameof(InputActionAsset), AssetName, "bindings",
                    $"Action '{name}' has {action.bindings.Count} binding(s); expected at least {minimumBindings}.");
            }
        }
    }
}