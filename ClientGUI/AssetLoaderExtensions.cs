using Rampastring.Tools;
using Rampastring.XNAUI;
using System.IO;

namespace ClientGUI
{
    public static class AssetLoaderExtensions
    {
        /// <summary>
        /// Looks up a file with the specified name in all <see cref="AssetLoader.AssetSearchPaths"/>.
        /// </summary>
        /// <param name="name">The name of the file to look up.</param>
        /// <returns>The file info if it was found, otherwise <c>null</c>.</returns>
        public static FileInfo GetFile(string name)
        {
            foreach (string searchPath in AssetLoader.AssetSearchPaths)
            {
                FileInfo fileInfo = SafePath.GetFile(searchPath, name);

                if (fileInfo.Exists)
                    return fileInfo;
            }

            Logger.Log($"AssetLoaderExtensions.GetFile: '{name}' not found. Searched:");
            foreach (string searchPath in AssetLoader.AssetSearchPaths)
                Logger.Log($"  - {SafePath.GetFile(searchPath, name).FullName}");

            return null;
        }
    }
}
