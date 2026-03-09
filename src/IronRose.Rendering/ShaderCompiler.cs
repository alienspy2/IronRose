using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;
using RoseEngine;

namespace IronRose.Rendering
{
    public static class ShaderCompiler
    {
        private static string? _cacheDir;

        public static void SetCacheDirectory(string path)
        {
            _cacheDir = path;
            Directory.CreateDirectory(path);
        }

        public static void ClearCache()
        {
            if (_cacheDir != null && Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
                Directory.CreateDirectory(_cacheDir);
                Debug.Log("[ShaderCompiler] Shader cache cleared");
            }
        }

        public static Shader CompileComputeGLSL(GraphicsDevice device, string computePath)
        {
            var computeBytes = GetCachedOrCompileSpirv(computePath, ShaderStages.Compute);
            var computeDesc = new ShaderDescription(ShaderStages.Compute, computeBytes, "main");

            try
            {
                return device.ResourceFactory.CreateFromSpirv(computeDesc);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderCompiler] ERROR compiling compute: {ex.Message}");
                throw;
            }
        }

        public static Shader[] CompileGLSL(GraphicsDevice device, string vertexPath, string fragmentPath)
        {
            var vertexBytes = GetCachedOrCompileSpirv(vertexPath, ShaderStages.Vertex);
            var fragmentBytes = GetCachedOrCompileSpirv(fragmentPath, ShaderStages.Fragment);

            var vertexDesc = new ShaderDescription(ShaderStages.Vertex, vertexBytes, "main");
            var fragmentDesc = new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "main");

            try
            {
                return device.ResourceFactory.CreateFromSpirv(vertexDesc, fragmentDesc);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ShaderCompiler] ERROR: {ex.Message}");
                throw;
            }
        }

        // ─── SPIR-V Cache ──────────────────────────────────────

        private static byte[] GetCachedOrCompileSpirv(string glslPath, ShaderStages stage)
        {
            string sourceText = File.ReadAllText(glslPath);

            if (_cacheDir == null)
                return Encoding.UTF8.GetBytes(sourceText);

            byte[] sourceBytes = Encoding.UTF8.GetBytes(sourceText);
            byte[] sourceHash = SHA256.HashData(sourceBytes);

            string cachePath = GetShaderCachePath(glslPath);

            // Cache hit
            try
            {
                if (TryLoadCachedSpirv(cachePath, sourceHash, out var spirvBytes))
                {
                    Debug.Log($"[ShaderCompiler] Cache hit: {Path.GetFileName(glslPath)}");
                    return spirvBytes;
                }
            }
            catch
            {
                TryDeleteFile(cachePath);
            }

            // Cache miss: compile GLSL → SPIR-V
            try
            {
                var result = SpirvCompilation.CompileGlslToSpirv(
                    sourceText, Path.GetFileName(glslPath), stage,
                    new GlslCompileOptions(false));

                SaveSpirvCache(cachePath, sourceHash, result.SpirvBytes);
                Debug.Log($"[ShaderCompiler] Compiled & cached: {Path.GetFileName(glslPath)}");
                return result.SpirvBytes;
            }
            catch (Exception ex)
            {
                // Fallback: return GLSL text bytes (original behavior)
                Debug.LogWarning($"[ShaderCompiler] SPIR-V pre-compile failed, using GLSL fallback: {ex.Message}");
                return sourceBytes;
            }
        }

        private static string GetShaderCachePath(string glslPath)
        {
            var fileName = Path.GetFileName(glslPath);
            return Path.Combine(_cacheDir!, fileName + ".spvcache");
        }

        private static bool TryLoadCachedSpirv(string cachePath, byte[] expectedHash, out byte[] spirvBytes)
        {
            spirvBytes = Array.Empty<byte>();

            if (!File.Exists(cachePath))
                return false;

            using var fs = File.OpenRead(cachePath);
            using var reader = new BinaryReader(fs);

            // Read and compare hash
            byte[] storedHash = reader.ReadBytes(32);
            if (storedHash.Length != 32)
                return false;

            for (int i = 0; i < 32; i++)
            {
                if (storedHash[i] != expectedHash[i])
                    return false;
            }

            // Read SPIR-V data
            int dataLen = reader.ReadInt32();
            if (dataLen <= 0 || dataLen > 10 * 1024 * 1024) // sanity: max 10MB
                return false;

            spirvBytes = reader.ReadBytes(dataLen);
            return spirvBytes.Length == dataLen;
        }

        private static void SaveSpirvCache(string cachePath, byte[] sourceHash, byte[] spirvBytes)
        {
            try
            {
                using var fs = File.Create(cachePath);
                using var writer = new BinaryWriter(fs);
                writer.Write(sourceHash);           // 32B SHA256
                writer.Write(spirvBytes.Length);     // 4B length
                writer.Write(spirvBytes);            // N×B SPIR-V
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ShaderCompiler] Failed to save cache: {ex.Message}");
                TryDeleteFile(cachePath);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
