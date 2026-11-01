using System.IO;
using Skyrift.Data;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// Shared authoring primitives for every Skyrift editor builder (TECH_DOC §3.1, §5.14).
    ///
    /// <b>Ownership rule.</b> Assets this toolchain generates are named with a <c>gb_</c> / <c>GB_</c>
    /// prefix ("greybox"). <see cref="Resolve{T}"/> only overwrites those when the caller explicitly asks
    /// for a rebuild; anything else on disk is hand-authored and is never touched. That is what makes the
    /// setup tool safe to re-run after an artist has replaced a placeholder: the GUID survives, so every
    /// scene and catalog reference to it survives too.
    ///
    /// Editor-only by construction (the assembly is Editor-platform), so the runtime bans on
    /// <c>GetComponent</c>, allocation and <c>Find*</c> (§11.2) do not apply here.
    /// </summary>
    public static class AssetAuthoring
    {
        /// <summary>Prefix marking an asset as generated placeholder content, replaceable without a code change.</summary>
        public const string GreyboxPrefix = "gb_";

        /// <summary>URP Lit is the only shader the greybox uses; §2.1 forbids built-in pipeline shaders.</summary>
        private const string LitShader = "Universal Render Pipeline/Lit";

        /// <summary>
        /// Loads the asset at <paramref name="path"/>, or creates it when missing. When
        /// <paramref name="overwrite"/> is true an existing asset is replaced <b>in place</b> via
        /// <c>CreateAsset</c> on the same path, which keeps its GUID and therefore every reference to it.
        /// </summary>
        public static T Resolve<T>(string path, bool overwrite = false) where T : ScriptableObject
        {
            SkyriftPaths.EnsureFolder(ParentOf(path));

            T existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing != null && !overwrite) return existing;

            T created = ScriptableObject.CreateInstance<T>();
            // CreateAsset on an existing path replaces the object but keeps the GUID, so scene and catalog
            // references survive a rebuild. No delete-then-create, which would break every reference.
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        /// <summary>
        /// Writes the immutable id and localization key of a definition. Both fields are <c>private</c> on
        /// <see cref="DefinitionBase"/> by design (§5: an id is never editable from gameplay code), so the
        /// only legal authoring route is the serialized property, not a setter.
        /// </summary>
        public static void SetIdentity(DefinitionBase definition, string id, string displayNameKey)
        {
            if (definition == null) return;

            var serialized = new SerializedObject(definition);
            SerializedProperty idProperty = serialized.FindProperty("id");
            SerializedProperty keyProperty = serialized.FindProperty("displayNameKey");

            if (idProperty != null) idProperty.stringValue = id;
            if (keyProperty != null) keyProperty.stringValue = displayNameKey;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        /// <summary>Marks an asset dirty so <see cref="SaveAll"/> writes it. Cheap; call after any field edit.</summary>
        public static void Touch(Object asset)
        {
            if (asset != null) EditorUtility.SetDirty(asset);
        }

        /// <summary>
        /// Writes every dirty asset once, at the end of a build. Saving per asset would reimport the project
        /// dozens of times and make a one-click setup take minutes instead of seconds.
        /// </summary>
        public static void SaveAll()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// Creates or updates a URP Lit placeholder material. SRP Batcher compatible by default, which is
        /// what §11.1's "no per-instance material clones" budget depends on: every greybox object of a given
        /// colour shares one material asset instead of instantiating its own.
        /// </summary>
        public static Material GreyboxMaterial(string name, Color baseColor, bool overwrite = false,
            float smoothness = 0.12f, Color emission = default)
        {
            string path = SkyriftPaths.Materials + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            Shader shader = Shader.Find(LitShader);
            if (shader == null)
            {
                // URP is not the active pipeline yet. ProjectSetup runs first in the one-click build, so this
                // is only reachable when a builder is invoked on its own; fall back loudly rather than
                // silently producing magenta geometry.
                Debug.LogError("[Skyrift] URP Lit shader not found. Run Tools > Skyrift > Setup Project first.");
                shader = Shader.Find("Hidden/InternalErrorShader");
            }

            bool created = material == null;
            if (created)
            {
                material = new Material(shader);
            }
            else if (overwrite)
            {
                material.shader = shader;
            }
            else
            {
                return material;   // hand-tuned: leave it alone
            }

            material.name = name;
            material.SetColor("_BaseColor", baseColor);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_Metallic", 0f);

            bool emissive = emission != default && emission.maxColorComponent > 0f;
            if (emissive)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", emission);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            if (created) AssetDatabase.CreateAsset(material, path);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Adds a primitive visual as a child. The collider is removed by default: greybox geometry is
        /// decoration, and §4.7 takes contacts from authored trigger volumes only. A stray mesh collider on
        /// a chunk would fight the closed-form jump physics for no benefit.
        /// </summary>
        public static GameObject Visual(Transform parent, string name, PrimitiveType shape,
            Vector3 localPosition, Vector3 localScale, Material material, bool keepCollider = false)
        {
            GameObject visual = GameObject.CreatePrimitive(shape);
            visual.name = name;
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = localPosition;
            visual.transform.localScale = localScale;

            // SetParent does not inherit the layer, and CreatePrimitive always starts on Default. Inheriting
            // it here keeps a rig's decoration on the same layer as its root, so a camera culling mask or a
            // future layer-based effect treats the whole object as one thing (§11.2 rule 6).
            if (parent != null) visual.layer = parent.gameObject.layer;

            if (!keepCollider)
            {
                Collider collider = visual.GetComponent<Collider>();
                if (collider != null) Object.DestroyImmediate(collider);
            }

            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null && material != null) renderer.sharedMaterial = material;

            return visual;
        }

        /// <summary>
        /// Saves a constructed hierarchy as a prefab, replacing an existing one in place so its GUID (and
        /// every PoolCatalog / scene reference to it) survives a rebuild.
        /// </summary>
        public static GameObject SavePrefab(GameObject instance, string path, bool overwrite)
        {
            SkyriftPaths.EnsureFolder(ParentOf(path));

            GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && !overwrite)
            {
                Object.DestroyImmediate(instance);
                return existing;
            }

            // PrefabUtility.SaveAsPrefabAsset replaces the asset at the path and keeps its GUID. It does not
            // connect the in-memory instance to the new prefab (that is SaveAsPrefabAssetAndConnect), so the
            // instance is destroyed here rather than being left behind in the open scene.
            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        /// <summary>Asset path of the folder containing <paramref name="path"/>.</summary>
        public static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "Assets";
            int slash = path.LastIndexOf('/');
            return slash <= 0 ? "Assets" : path.Substring(0, slash);
        }

        /// <summary>
        /// Maps a Unity asset path to a file-system path, for the few builders that must write a real file
        /// (a WAV, a mixer's YAML) rather than a serialized object. Unity's working directory is normally the
        /// project root, but deriving it from <c>Application.dataPath</c> means the tool also works when run
        /// from a batch-mode process whose CWD is elsewhere. Returns null when the path cannot be resolved.
        /// </summary>
        public static string FullPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            string dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath)) return null;

            DirectoryInfo projectRoot = Directory.GetParent(dataPath);
            return projectRoot == null ? null : Path.Combine(projectRoot.FullName, assetPath);
        }
    }
}