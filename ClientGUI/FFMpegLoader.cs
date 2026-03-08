using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ClientGUI
{
    public static class FFMpegLoader
    {
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            // Tell AutoGen where to load native FFmpeg libraries from
            ffmpeg.RootPath = GetBasePath();

            NativeLibrary.SetDllImportResolver(typeof(FFMpegLoader).Assembly, Resolve);

            // Optional: also add to PATH so Windows loader can resolve transitive deps
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!path.Contains(ffmpeg.RootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", ffmpeg.RootPath + ";" + path);
                }
            }

            _initialized = true;
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? path)
        {
            // Try exact file name first.
            string candidate = Path.Combine(ffmpeg.RootPath, libraryName);
            if (File.Exists(candidate))
            {
                if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
                {
                    return handle;
                }
            }

            // Fallback: try prefix/suffix variants commonly used by FFmpeg.
            foreach (string file in Directory.Exists(ffmpeg.RootPath) ? Directory.GetFiles(ffmpeg.RootPath) : Array.Empty<string>())
            {
                if (file.Contains(Normalize(libraryName), StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeLibrary.TryLoad(file, out IntPtr handle))
                    {
                        return handle;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private static string Normalize(string name)
        {
            // Use GetFileNameWithoutExtension to avoid greedy substring replacement
            // (e.g., ".so" inside "resource" would be incorrectly stripped by Replace).
            string ext = Path.GetExtension(name);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(name);
            }

            return name;
        }

        private static string GetBasePath()
        {
            return Path.Combine(AppContext.BaseDirectory, "runtimes", GetRidFolder(), "native");
        }

        private static string GetRidFolder()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return RuntimeInformation.OSArchitecture == Architecture.X86 ? "win-x86" : "win-x64";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return RuntimeInformation.OSArchitecture == Architecture.X86 ? "linux-x86" : "linux-x64";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "osx-x64";
            }

            return "unknown";
        }
    }
}
