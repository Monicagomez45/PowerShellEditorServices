﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.PowerShell.EditorServices.Services.TextDocument;
using Microsoft.PowerShell.EditorServices.Services.Workspace;
using Microsoft.PowerShell.EditorServices.Utility;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Microsoft.PowerShell.EditorServices.Services
{
    /// <summary>
    /// Manages a "workspace" of script files that are open for a particular
    /// editing session.  Also helps to navigate references between ScriptFiles.
    /// </summary>
    internal class WorkspaceService
    {
        #region Private Fields

        // List of all file extensions considered PowerShell files in the .Net Core Framework.
        private static readonly string[] s_psFileExtensionsCoreFramework =
        {
            ".ps1",
            ".psm1",
            ".psd1"
        };

        // .Net Core doesn't appear to use the same three letter pattern matching rule although the docs
        // suggest it should be find the '.ps1xml' files because we search for the pattern '*.ps1'.
        // ref https://docs.microsoft.com/en-us/dotnet/api/system.io.directory.getfiles?view=netcore-2.1#System_IO_Directory_GetFiles_System_String_System_String_System_IO_EnumerationOptions_
        private static readonly string[] s_psFileExtensionsFullFramework =
        {
            ".ps1",
            ".psm1",
            ".psd1",
            ".ps1xml"
        };

        // An array of globs which includes everything.
        private static readonly string[] s_psIncludeAllGlob = new[]
        {
            "**/*"
        };

        private readonly ILogger logger;
        private readonly Version powerShellVersion;
        private readonly ConcurrentDictionary<string, ScriptFile> workspaceFiles = new();

        #endregion

        #region Properties

        /// <summary>
        /// Gets or sets the root path of the workspace.
        /// </summary>
        public string WorkspacePath { get; set; }

        /// <summary>
        /// Gets or sets the default list of file globs to exclude during workspace searches.
        /// </summary>
        public List<string> ExcludeFilesGlob { get; set; }

        /// <summary>
        /// Gets or sets whether the workspace should follow symlinks in search operations.
        /// </summary>
        public bool FollowSymlinks { get; set; }

        #endregion

        #region Constructors

        /// <summary>
        /// Creates a new instance of the Workspace class.
        /// </summary>
        public WorkspaceService(ILoggerFactory factory)
        {
            powerShellVersion = VersionUtils.PSVersion;
            logger = factory.CreateLogger<WorkspaceService>();
            ExcludeFilesGlob = new List<string>();
            FollowSymlinks = true;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets an open file in the workspace. If the file isn't open but exists on the filesystem, load and return it.
        /// <para>IMPORTANT: Not all documents have a backing file e.g. untitled: scheme documents.  Consider using
        /// <see cref="TryGetFile(string, out ScriptFile)"/> instead.</para>
        /// </summary>
        /// <param name="filePath">The file path at which the script resides.</param>
        /// <exception cref="FileNotFoundException">
        /// <paramref name="filePath"/> is not found.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="filePath"/> contains a null or empty string.
        /// </exception>
        public ScriptFile GetFile(string filePath) => GetFile(new Uri(filePath));

        public ScriptFile GetFile(Uri fileUri) => GetFile(DocumentUri.From(fileUri));

        /// <summary>
        /// Gets an open file in the workspace. If the file isn't open but exists on the filesystem, load and return it.
        /// <para>IMPORTANT: Not all documents have a backing file e.g. untitled: scheme documents.  Consider using
        /// <see cref="TryGetFile(string, out ScriptFile)"/> instead.</para>
        /// </summary>
        /// <param name="documentUri">The document URI at which the script resides.</param>
        /// <exception cref="FileNotFoundException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public ScriptFile GetFile(DocumentUri documentUri)
        {
            Validate.IsNotNull(nameof(documentUri), documentUri);

            string keyName = GetFileKey(documentUri);

            // Make sure the file isn't already loaded into the workspace
            if (!workspaceFiles.TryGetValue(keyName, out ScriptFile scriptFile))
            {
                // This method allows FileNotFoundException to bubble up
                // if the file isn't found.
                using (StreamReader streamReader = OpenStreamReader(documentUri))
                {
                    scriptFile =
                        new ScriptFile(
                            documentUri,
                            streamReader,
                            powerShellVersion);

                    workspaceFiles[keyName] = scriptFile;
                }

                logger.LogDebug("Opened file on disk: " + documentUri.ToString());
            }

            return scriptFile;
        }

        /// <summary>
        /// Tries to get an open file in the workspace. Returns true if it succeeds, false otherwise.
        /// </summary>
        /// <param name="filePath">The file path at which the script resides.</param>
        /// <param name="scriptFile">The out parameter that will contain the ScriptFile object.</param>
        public bool TryGetFile(string filePath, out ScriptFile scriptFile)
        {
            // This might not have been given a file path, in which case the Uri constructor barfs.
            try
            {
                return TryGetFile(new Uri(filePath), out scriptFile);
            }
            catch (UriFormatException)
            {
                scriptFile = null;
                return false;
            }
        }

        /// <summary>
        /// Tries to get an open file in the workspace. Returns true if it succeeds, false otherwise.
        /// </summary>
        /// <param name="fileUri">The file uri at which the script resides.</param>
        /// <param name="scriptFile">The out parameter that will contain the ScriptFile object.</param>
        public bool TryGetFile(Uri fileUri, out ScriptFile scriptFile) =>
            TryGetFile(DocumentUri.From(fileUri), out scriptFile);

        /// <summary>
        /// Tries to get an open file in the workspace. Returns true if it succeeds, false otherwise.
        /// </summary>
        /// <param name="documentUri">The file uri at which the script resides.</param>
        /// <param name="scriptFile">The out parameter that will contain the ScriptFile object.</param>
        public bool TryGetFile(DocumentUri documentUri, out ScriptFile scriptFile)
        {
            switch (documentUri.Scheme)
            {
                // List supported schemes here
                case "file":
                case "inmemory":
                case "untitled":
                case "vscode-notebook-cell":
                    break;

                default:
                    scriptFile = null;
                    return false;
            }

            try
            {
                scriptFile = GetFile(documentUri);
                return true;
            }
            catch (Exception e) when (
                e is NotSupportedException or
                FileNotFoundException or
                DirectoryNotFoundException or
                PathTooLongException or
                IOException or
                SecurityException or
                UnauthorizedAccessException)
            {
                logger.LogWarning($"Failed to get file for fileUri: '{documentUri}'", e);
                scriptFile = null;
                return false;
            }
        }

        /// <summary>
        /// Gets a new ScriptFile instance which is identified by the given file path.
        /// </summary>
        /// <param name="filePath">The file path for which a buffer will be retrieved.</param>
        /// <returns>A ScriptFile instance if there is a buffer for the path, null otherwise.</returns>
        public ScriptFile GetFileBuffer(string filePath) => GetFileBuffer(filePath, initialBuffer: null);

        /// <summary>
        /// Gets a new ScriptFile instance which is identified by the given file
        /// path and initially contains the given buffer contents.
        /// </summary>
        /// <param name="filePath">The file path for which a buffer will be retrieved.</param>
        /// <param name="initialBuffer">The initial buffer contents if there is not an existing ScriptFile for this path.</param>
        /// <returns>A ScriptFile instance for the specified path.</returns>
        public ScriptFile GetFileBuffer(string filePath, string initialBuffer) => GetFileBuffer(new Uri(filePath), initialBuffer);

        /// <summary>
        /// Gets a new ScriptFile instance which is identified by the given file path.
        /// </summary>
        /// <param name="fileUri">The file Uri for which a buffer will be retrieved.</param>
        /// <returns>A ScriptFile instance if there is a buffer for the path, null otherwise.</returns>
        public ScriptFile GetFileBuffer(Uri fileUri) => GetFileBuffer(fileUri, initialBuffer: null);

        /// <summary>
        /// Gets a new ScriptFile instance which is identified by the given file
        /// path and initially contains the given buffer contents.
        /// </summary>
        /// <param name="fileUri">The file Uri for which a buffer will be retrieved.</param>
        /// <param name="initialBuffer">The initial buffer contents if there is not an existing ScriptFile for this path.</param>
        /// <returns>A ScriptFile instance for the specified path.</returns>
        public ScriptFile GetFileBuffer(Uri fileUri, string initialBuffer) => GetFileBuffer(DocumentUri.From(fileUri), initialBuffer);

        /// <summary>
        /// Gets a new ScriptFile instance which is identified by the given file
        /// path and initially contains the given buffer contents.
        /// </summary>
        /// <param name="documentUri">The file Uri for which a buffer will be retrieved.</param>
        /// <param name="initialBuffer">The initial buffer contents if there is not an existing ScriptFile for this path.</param>
        /// <returns>A ScriptFile instance for the specified path.</returns>
        public ScriptFile GetFileBuffer(DocumentUri documentUri, string initialBuffer)
        {
            Validate.IsNotNull(nameof(documentUri), documentUri);

            string keyName = GetFileKey(documentUri);

            // Make sure the file isn't already loaded into the workspace
            if (!workspaceFiles.TryGetValue(keyName, out ScriptFile scriptFile) && initialBuffer != null)
            {
                scriptFile =
                    new ScriptFile(
                        documentUri,
                        initialBuffer,
                        powerShellVersion);

                workspaceFiles[keyName] = scriptFile;

                logger.LogDebug("Opened file as in-memory buffer: " + documentUri.ToString());
            }

            return scriptFile;
        }

        /// <summary>
        /// Gets an array of all opened ScriptFiles in the workspace.
        /// </summary>
        /// <returns>An array of all opened ScriptFiles in the workspace.</returns>
        public ScriptFile[] GetOpenedFiles() => workspaceFiles.Values.ToArray();

        /// <summary>
        /// Closes a currently open script file with the given file path.
        /// </summary>
        /// <param name="scriptFile">The file path at which the script resides.</param>
        public void CloseFile(ScriptFile scriptFile)
        {
            Validate.IsNotNull(nameof(scriptFile), scriptFile);

            string keyName = GetFileKey(scriptFile.DocumentUri);
            workspaceFiles.TryRemove(keyName, out ScriptFile _);
        }

        /// <summary>
        /// Gets all file references by recursively searching
        /// through referenced files in a scriptfile
        /// </summary>
        /// <param name="scriptFile">Contains the details and contents of an open script file</param>
        /// <returns>A scriptfile array where the first file
        /// in the array is the "root file" of the search</returns>
        public ScriptFile[] ExpandScriptReferences(ScriptFile scriptFile)
        {
            Dictionary<string, ScriptFile> referencedScriptFiles = new();
            List<ScriptFile> expandedReferences = new();

            // add original file so it's not searched for, then find all file references
            referencedScriptFiles.Add(scriptFile.Id, scriptFile);
            RecursivelyFindReferences(scriptFile, referencedScriptFiles);

            // remove original file from referenced file and add it as the first element of the
            // expanded referenced list to maintain order so the original file is always first in the list
            referencedScriptFiles.Remove(scriptFile.Id);
            expandedReferences.Add(scriptFile);

            if (referencedScriptFiles.Count > 0)
            {
                expandedReferences.AddRange(referencedScriptFiles.Values);
            }

            return expandedReferences.ToArray();
        }

        /// <summary>
        /// Gets the workspace-relative path of the given file path.
        /// </summary>
        /// <param name="filePath">The original full file path.</param>
        /// <returns>A relative file path</returns>
        public string GetRelativePath(string filePath)
        {
            string resolvedPath = filePath;

            if (!IsPathInMemory(filePath) && !string.IsNullOrEmpty(WorkspacePath))
            {
                Uri workspaceUri = new(WorkspacePath);
                Uri fileUri = new(filePath);

                resolvedPath = workspaceUri.MakeRelativeUri(fileUri).ToString();

                // Convert the directory separators if necessary
                if (Path.DirectorySeparatorChar == '\\')
                {
                    resolvedPath = resolvedPath.Replace('/', '\\');
                }
            }

            return resolvedPath;
        }

        /// <summary>
        /// Enumerate all the PowerShell (ps1, psm1, psd1) files in the workspace in a recursive manner, using default values.
        /// </summary>
        /// <returns>An enumerator over the PowerShell files found in the workspace.</returns>
        public IEnumerable<string> EnumeratePSFiles()
        {
            return EnumeratePSFiles(
                ExcludeFilesGlob.ToArray(),
                s_psIncludeAllGlob,
                maxDepth: 64,
                ignoreReparsePoints: !FollowSymlinks
            );
        }

        /// <summary>
        /// Enumerate all the PowerShell (ps1, psm1, psd1) files in the workspace in a recursive manner.
        /// </summary>
        /// <returns>An enumerator over the PowerShell files found in the workspace.</returns>
        public IEnumerable<string> EnumeratePSFiles(
            string[] excludeGlobs,
            string[] includeGlobs,
            int maxDepth,
            bool ignoreReparsePoints
        )
        {
            if (WorkspacePath == null || !Directory.Exists(WorkspacePath))
            {
                yield break;
            }

            Matcher matcher = new();
            foreach (string pattern in includeGlobs) { matcher.AddInclude(pattern); }
            foreach (string pattern in excludeGlobs) { matcher.AddExclude(pattern); }

            WorkspaceFileSystemWrapperFactory fsFactory = new(
                WorkspacePath,
                maxDepth,
                VersionUtils.IsNetCore ? s_psFileExtensionsCoreFramework : s_psFileExtensionsFullFramework,
                ignoreReparsePoints,
                logger
            );
            PatternMatchingResult fileMatchResult = matcher.Execute(fsFactory.RootDirectory);
            foreach (FilePatternMatch item in fileMatchResult.Files)
            {
                // item.Path always contains forward slashes in paths when it should be backslashes on Windows.
                // Since we're returning strings here, it's important to use the correct directory separator.
                string path = VersionUtils.IsWindows ? item.Path.Replace('/', Path.DirectorySeparatorChar) : item.Path;
                yield return Path.Combine(WorkspacePath, path);
            }
        }

        #endregion

        #region Private Methods
        /// <summary>
        /// Recursively searches through referencedFiles in scriptFiles
        /// and builds a Dictionary of the file references
        /// </summary>
        /// <param name="scriptFile">Details an contents of "root" script file</param>
        /// <param name="referencedScriptFiles">A Dictionary of referenced script files</param>
        private void RecursivelyFindReferences(
            ScriptFile scriptFile,
            Dictionary<string, ScriptFile> referencedScriptFiles)
        {
            // Get the base path of the current script for use in resolving relative paths
            string baseFilePath = scriptFile.IsInMemory
                ? WorkspacePath
                : Path.GetDirectoryName(scriptFile.FilePath);

            foreach (string referencedFileName in scriptFile.ReferencedFiles)
            {
                string resolvedScriptPath =
                    ResolveRelativeScriptPath(
                        baseFilePath,
                        referencedFileName);

                // If there was an error resolving the string, skip this reference
                if (resolvedScriptPath == null)
                {
                    continue;
                }

                logger.LogDebug(
                    string.Format(
                        "Resolved relative path '{0}' to '{1}'",
                        referencedFileName,
                        resolvedScriptPath));

                // Get the referenced file if it's not already in referencedScriptFiles
                if (TryGetFile(resolvedScriptPath, out ScriptFile referencedFile))
                {
                    // Normalize the resolved script path and add it to the
                    // referenced files list if it isn't there already
                    resolvedScriptPath = resolvedScriptPath.ToLower();
                    if (!referencedScriptFiles.ContainsKey(resolvedScriptPath))
                    {
                        referencedScriptFiles.Add(resolvedScriptPath, referencedFile);
                        RecursivelyFindReferences(referencedFile, referencedScriptFiles);
                    }
                }
            }
        }

        internal static StreamReader OpenStreamReader(DocumentUri uri)
        {
            FileStream fileStream = new(uri.GetFileSystemPath(), FileMode.Open, FileAccess.Read);
            // Default to UTF8 no BOM if a BOM is not present. Note that `Encoding.UTF8` is *with*
            // BOM, so we call the ctor here to get the BOM-less version.
            //
            // TODO: Honor workspace encoding settings for the fallback.
            return new StreamReader(fileStream, new UTF8Encoding(), detectEncodingFromByteOrderMarks: true);
        }

        internal static string ReadFileContents(DocumentUri uri)
        {
            using StreamReader reader = OpenStreamReader(uri);
            return reader.ReadToEnd();
        }

        internal static bool IsPathInMemory(string filePath)
        {
            bool isInMemory = false;

            // In cases where a "virtual" file is displayed in the editor,
            // we need to treat the file differently than one that exists
            // on disk.  A virtual file could be something like a diff
            // view of the current file or an untitled file.
            try
            {
                // File system absolute paths will have a URI scheme of file:.
                // Other schemes like "untitled:" and "gitlens-git:" will return false for IsFile.
                Uri uri = new(filePath);
                isInMemory = !uri.IsFile;
            }
            catch (UriFormatException)
            {
                // Relative file paths cause a UriFormatException.
                // In this case, fallback to using Path.GetFullPath().
                try
                {
                    Path.GetFullPath(filePath);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    isInMemory = true;
                }
                catch (PathTooLongException)
                {
                    // If we ever get here, it should be an actual file so, not in memory
                }
            }

            return isInMemory;
        }

        internal string ResolveWorkspacePath(string path) => ResolveRelativeScriptPath(WorkspacePath, path);

        internal string ResolveRelativeScriptPath(string baseFilePath, string relativePath)
        {
            string combinedPath = null;
            Exception resolveException = null;

            try
            {
                // If the path is already absolute there's no need to resolve it relatively
                // to the baseFilePath.
                if (Path.IsPathRooted(relativePath))
                {
                    return relativePath;
                }

                // Get the directory of the original script file, combine it
                // with the given path and then resolve the absolute file path.
                combinedPath =
                    Path.GetFullPath(
                        Path.Combine(
                            baseFilePath,
                            relativePath));
            }
            catch (NotSupportedException e)
            {
                // Occurs if the path is incorrectly formatted for any reason.  One
                // instance where this occurred is when a user had curly double-quote
                // characters in their source instead of normal double-quotes.
                resolveException = e;
            }
            catch (ArgumentException e)
            {
                // Occurs if the path contains invalid characters, specifically those
                // listed in System.IO.Path.InvalidPathChars.
                resolveException = e;
            }

            if (resolveException != null)
            {
                logger.LogError(
                    "Could not resolve relative script path\r\n" +
                    $"    baseFilePath = {baseFilePath}\r\n    " +
                    $"    relativePath = {relativePath}\r\n\r\n" +
                    $"{resolveException}");
            }

            return combinedPath;
        }

        /// <summary>
        /// Returns a normalized string for a given documentUri to be used as key name.
        /// Case-sensitive uri on Linux and lowercase for other platforms.
        /// </summary>
        /// <param name="documentUri">A DocumentUri object to get a normalized key name from</param>
        private static string GetFileKey(DocumentUri documentUri)
            => VersionUtils.IsLinux ? documentUri.ToString() : documentUri.ToString().ToLower();

        #endregion
    }
}
