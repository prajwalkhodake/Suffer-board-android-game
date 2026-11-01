using System.IO;
using UnityEditor;
using UnityEngine;

namespace Skyrift.Editor
{
    /// <summary>
    /// Encodes generated placeholder audio as a real 16-bit mono PCM <c>.wav</c> on disk.
    ///
    /// A file, not <c>AudioClip.Create</c>: an in-memory clip cannot be saved as an audio asset, and a clip
    /// saved through <c>AssetDatabase.CreateAsset</c> becomes a <c>.asset</c> that no audio tool can open,
    /// re-import or replace. Writing a WAV means an artist drops a finished file over the placeholder at the
    /// same path and every reference keeps working — which is the whole point of the placeholder audio bank
    /// (TECH_DOC §4.18 owns the bank; the brief requires placeholders to be labelled and replaceable).
    ///
    /// Mono 22050 Hz is deliberate. Placeholder SFX are 2D and short, so stereo and 44.1 kHz would quadruple
    /// the bytes for no audible difference, and §11.1's texture/audio budget is easier to hold with headroom.
    /// </summary>
    internal static class WavWriter
    {
        /// <summary>Sample rate every generated clip is authored at. Music stems must all match to stay aligned.</summary>
        public const int SampleRate = 22050;

        private const int BitsPerSample = 16;
        private const int Channels = 1;

        /// <summary>
        /// Writes the samples and imports the result. Returns the imported clip, or null when the write or
        /// the import failed — the caller reports that rather than assigning a null clip silently.
        /// </summary>
        /// <param name="assetPath">Unity asset path, e.g. "Assets/_Project/Audio/SFX/sfx_jump.wav".</param>
        /// <param name="samples">Mono samples in [-1, 1]. Values outside the range are clamped, not wrapped.</param>
        public static AudioClip Write(string assetPath, float[] samples)
        {
            if (string.IsNullOrEmpty(assetPath) || samples == null || samples.Length == 0)
            {
                Debug.LogError($"[Skyrift] Cannot write '{assetPath}': no samples.");
                return null;
            }

            SkyriftPaths.EnsureFolder(AssetAuthoring.ParentOf(assetPath));

            string fullPath = AssetAuthoring.FullPath(assetPath);
            if (fullPath == null)
            {
                Debug.LogError($"[Skyrift] Cannot resolve a file-system path for '{assetPath}'.");
                return null;
            }

            try
            {
                File.WriteAllBytes(fullPath, Encode(samples));
            }
            catch (IOException e)
            {
                Debug.LogError($"[Skyrift] Failed to write '{assetPath}': {e.Message}");
                return null;
            }

            // ForceSynchronousImport: the catalog rows are written immediately afterwards and need a real
            // AudioClip reference, not a placeholder that resolves on the next editor tick.
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
        }

        /// <summary>
        /// Canonical RIFF/WAVE PCM encoding. Written by hand rather than pulled from a package: 44 bytes of
        /// header is not a dependency worth taking, and the format is fixed.
        /// </summary>
        private static byte[] Encode(float[] samples)
        {
            int dataSize = samples.Length * (BitsPerSample / 8) * Channels;
            int byteRate = SampleRate * Channels * (BitsPerSample / 8);
            int blockAlign = Channels * (BitsPerSample / 8);

            using (var stream = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);          // RIFF chunk size: everything after this field
                writer.Write(new[] { 'W', 'A', 'V', 'E' });

                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);                     // PCM fmt chunk is always 16 bytes
                writer.Write((short)1);               // audio format 1 = uncompressed PCM
                writer.Write((short)Channels);
                writer.Write(SampleRate);
                writer.Write(byteRate);
                writer.Write((short)blockAlign);
                writer.Write((short)BitsPerSample);

                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                for (int i = 0; i < samples.Length; i++)
                {
                    // 32767 rather than 32768: the positive full-scale of a signed 16-bit sample is 32767,
                    // and scaling by 32768 would wrap a +1.0 sample to -32768 (an audible click).
                    float clamped = Mathf.Clamp(samples[i], -1f, 1f);
                    writer.Write((short)Mathf.RoundToInt(clamped * 32767f));
                }

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}