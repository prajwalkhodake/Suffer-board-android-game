using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// The authored physics layer layout (TECH_DOC §11.2 rule 6). Layers are infrastructure, not content:
    /// the collision model in §4.7 reads trigger volumes and the pure resolver, so the only pairs that may
    /// ever generate a contact are Player↔Obstacle and (from M2) Player↔Collectible. Everything else is
    /// disabled so the broadphase never spends time on pairs that cannot produce a gameplay event.
    ///
    /// Unity reserves layers 0–7; the first user layer is 8. Indices are fixed constants rather than a
    /// name lookup at runtime, because <c>LayerMask.NameToLayer</c> allocates a string and §11.2 forbids it
    /// on a per-frame path.
    /// </summary>
    public static class SkyriftLayers
    {
        public const int Player = 8;
        public const int Obstacle = 9;
        public const int Track = 10;
        public const int Collectible = 11;
        public const int Chaser = 12;
        public const int Vfx = 13;

        /// <summary>Indexed by layer number. Only the authored layers are named; the rest stay empty.</summary>
        private static readonly string[] Names =
        {
            null, null, null, null, null, null, null, null,
            "Player", "Obstacle", "Track", "Collectible", "Chaser", "Vfx"
        };

        /// <summary>
        /// Writes the layer names into TagManager.asset and disables the unused collision pairs.
        ///
        /// TagManager is edited through <c>SerializedObject</c> because <c>UnityEditorInternal.InternalEditorUtility</c>
        /// (the only API that adds a layer by name) is internal and version-fragile. The serialized array is
        /// a stable, documented shape.
        /// </summary>
        public static void Ensure()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[Skyrift] TagManager.asset not found; layers were not authored.");
                return;
            }

            var serialized = new SerializedObject(assets[0]);
            SerializedProperty layers = serialized.FindProperty("layers");
            if (layers == null)
            {
                Debug.LogError("[Skyrift] TagManager.asset has no 'layers' property; unexpected Unity version.");
                return;
            }

            for (int i = 0; i < Names.Length && i < layers.arraySize; i++)
            {
                if (Names[i] == null) continue;

                SerializedProperty element = layers.GetArrayElementAtIndex(i);
                // Never steal a layer another tool already named: a silent rename would move every collider
                // in the project onto a different layer and break contacts in a way that is hard to trace.
                if (!string.IsNullOrEmpty(element.stringValue) && element.stringValue != Names[i])
                {
                    Debug.LogWarning($"[Skyrift] Layer {i} is already '{element.stringValue}'; " +
                                     $"expected '{Names[i]}'. Resolve the clash before building the greybox.");
                    continue;
                }

                element.stringValue = Names[i];
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();

            EnsureCollisionMatrix();
        }

        /// <summary>
        /// Disables every custom-layer pair except the sanctioned contact pairs. Built-in layers are left at
        /// Unity's defaults: nothing the greybox authors lives on them, so changing them would be an
        /// unexplained project-wide edit.
        /// </summary>
        private static void EnsureCollisionMatrix()
        {
            for (int a = 8; a < Names.Length; a++)
            {
                if (Names[a] == null) continue;

                for (int b = a; b < Names.Length; b++)
                {
                    if (Names[b] == null) continue;
                    if (IsContactPair(a, b)) continue;

                    Physics.IgnoreLayerCollision(a, b, true);
                }
            }

            // Re-assert the sanctioned pairs. IgnoreLayerCollision is persistent project state, so a previous
            // run of this tool (or a hand edit in the Physics window) could have left them disabled.
            Physics.IgnoreLayerCollision(Player, Obstacle, false);
            Physics.IgnoreLayerCollision(Player, Collectible, false);
        }

        /// <summary>
        /// The only pairs that may produce a contact. Player↔Player and Obstacle↔Obstacle are deliberately
        /// absent: there is one player rig, and obstacles never interact with each other (§4.7).
        /// </summary>
        private static bool IsContactPair(int a, int b)
        {
            if (a == Player && b == Obstacle) return true;
            if (a == Player && b == Collectible) return true;
            return false;
        }
    }
}