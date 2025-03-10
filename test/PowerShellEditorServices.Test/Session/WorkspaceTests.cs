﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerShell.EditorServices.Services;
using Microsoft.PowerShell.EditorServices.Test.Shared;
using Microsoft.PowerShell.EditorServices.Services.TextDocument;
using Xunit;

namespace PowerShellEditorServices.Test.Session
{
    [Trait("Category", "Workspace")]
    public class WorkspaceTests
    {
        private static readonly Lazy<string> s_lazyDriveLetter = new(() => Path.GetFullPath("\\").Substring(0, 1));

        public static string CurrentDriveLetter => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? s_lazyDriveLetter.Value
            : string.Empty;

        [Fact]
        public void CanResolveWorkspaceRelativePath()
        {
            string workspacePath = TestUtilities.NormalizePath("c:/Test/Workspace/");
            string testPathInside = TestUtilities.NormalizePath("c:/Test/Workspace/SubFolder/FilePath.ps1");
            string testPathOutside = TestUtilities.NormalizePath("c:/Test/PeerPath/FilePath.ps1");
            string testPathAnotherDrive = TestUtilities.NormalizePath("z:/TryAndFindMe/FilePath.ps1");

            WorkspaceService workspace = new(NullLoggerFactory.Instance);

            // Test without a workspace path
            Assert.Equal(testPathOutside, workspace.GetRelativePath(testPathOutside));

            string expectedInsidePath = TestUtilities.NormalizePath("SubFolder/FilePath.ps1");
            string expectedOutsidePath = TestUtilities.NormalizePath("../PeerPath/FilePath.ps1");

            // Test with a workspace path
            workspace.WorkspacePath = workspacePath;
            Assert.Equal(expectedInsidePath, workspace.GetRelativePath(testPathInside));
            Assert.Equal(expectedOutsidePath, workspace.GetRelativePath(testPathOutside));
            Assert.Equal(testPathAnotherDrive, workspace.GetRelativePath(testPathAnotherDrive));
        }

        internal static WorkspaceService FixturesWorkspace()
        {
            return new WorkspaceService(NullLoggerFactory.Instance)
            {
                WorkspacePath = TestUtilities.NormalizePath("Fixtures/Workspace")
            };
        }

        // These are the default values for the EnumeratePSFiles() method
        // in Microsoft.PowerShell.EditorServices.Workspace class
        private static readonly string[] s_defaultExcludeGlobs = Array.Empty<string>();
        private static readonly string[] s_defaultIncludeGlobs = new[] { "**/*" };
        private const int s_defaultMaxDepth = 64;
        private const bool s_defaultIgnoreReparsePoints = false;

        internal static List<string> ExecuteEnumeratePSFiles(
            WorkspaceService workspace,
            string[] excludeGlobs,
            string[] includeGlobs,
            int maxDepth,
            bool ignoreReparsePoints)
        {
            List<string> fileList = new(workspace.EnumeratePSFiles(
                excludeGlobs: excludeGlobs,
                includeGlobs: includeGlobs,
                maxDepth: maxDepth,
                ignoreReparsePoints: ignoreReparsePoints
            ));

            // Assume order is not important from EnumeratePSFiles and sort the array so we can use
            // deterministic asserts
            fileList.Sort();
            return fileList;
        }

        [Fact]
        public void CanRecurseDirectoryTree()
        {
            WorkspaceService workspace = FixturesWorkspace();
            List<string> actual = ExecuteEnumeratePSFiles(
                workspace: workspace,
                excludeGlobs: s_defaultExcludeGlobs,
                includeGlobs: s_defaultIncludeGlobs,
                maxDepth: s_defaultMaxDepth,
                ignoreReparsePoints: s_defaultIgnoreReparsePoints
            );

            List<string> expected = new()
            {
                Path.Combine(workspace.WorkspacePath, "nested", "donotfind.ps1"),
                Path.Combine(workspace.WorkspacePath, "nested", "nestedmodule.psd1"),
                Path.Combine(workspace.WorkspacePath, "nested", "nestedmodule.psm1"),
                Path.Combine(workspace.WorkspacePath, "rootfile.ps1")
            };

            // .NET Core doesn't appear to use the same three letter pattern matching rule although the docs
            // suggest it should be find the '.ps1xml' files because we search for the pattern '*.ps1'
            // ref https://docs.microsoft.com/en-us/dotnet/api/system.io.directory.getfiles?view=netcore-2.1#System_IO_Directory_GetFiles_System_String_System_String_System_IO_EnumerationOptions_
            if (RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework"))
            {
                expected.Insert(3, Path.Combine(workspace.WorkspacePath, "other", "other.ps1xml"));
            }

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void CanRecurseDirectoryTreeWithLimit()
        {
            WorkspaceService workspace = FixturesWorkspace();
            List<string> actual = ExecuteEnumeratePSFiles(
                workspace: workspace,
                excludeGlobs: s_defaultExcludeGlobs,
                includeGlobs: s_defaultIncludeGlobs,
                maxDepth: 1,
                ignoreReparsePoints: s_defaultIgnoreReparsePoints
            );
            Assert.Equal(new[] { Path.Combine(workspace.WorkspacePath, "rootfile.ps1") }, actual);
        }

        [Fact]
        public void CanRecurseDirectoryTreeWithGlobs()
        {
            WorkspaceService workspace = FixturesWorkspace();
            List<string> actual = ExecuteEnumeratePSFiles(
                workspace: workspace,
                excludeGlobs: new[] { "**/donotfind*" },          // Exclude any files starting with donotfind
                includeGlobs: new[] { "**/*.ps1", "**/*.psd1" }, // Only include PS1 and PSD1 files
                maxDepth: s_defaultMaxDepth,
                ignoreReparsePoints: s_defaultIgnoreReparsePoints
            );

            Assert.Equal(new[] {
                    Path.Combine(workspace.WorkspacePath, "nested", "nestedmodule.psd1"),
                    Path.Combine(workspace.WorkspacePath, "rootfile.ps1")
                }, actual);
        }

        [Fact]
        public void CanDetermineIsPathInMemory()
        {
            string tempDir = Path.GetTempPath();
            string shortDirPath = Path.Combine(tempDir, "GitHub", "PowerShellEditorServices");
            string shortFilePath = Path.Combine(shortDirPath, "foo.ps1");
            const string shortUriForm = "git:/c%3A/Users/Keith/GitHub/dahlbyk/posh-git/src/PoshGitTypes.ps1?%7B%22path%22%3A%22c%3A%5C%5CUsers%5C%5CKeith%5C%5CGitHub%5C%5Cdahlbyk%5C%5Cposh-git%5C%5Csrc%5C%5CPoshGitTypes.ps1%22%2C%22ref%22%3A%22~%22%7D";
            const string longUriForm = "gitlens-git:c%3A%5CUsers%5CKeith%5CGitHub%5Cdahlbyk%5Cposh-git%5Csrc%5CPoshGitTypes%3Ae0022701.ps1?%7B%22fileName%22%3A%22src%2FPoshGitTypes.ps1%22%2C%22repoPath%22%3A%22c%3A%2FUsers%2FKeith%2FGitHub%2Fdahlbyk%2Fposh-git%22%2C%22sha%22%3A%22e0022701fa12e0bc22d0458673d6443c942b974a%22%7D";

            string[] inMemoryPaths = new[] {
                // Test short non-file paths
                "untitled:untitled-1",
                shortUriForm,
                "inmemory://foo.ps1",
                // Test long non-file path
                longUriForm
            };

            Assert.All(inMemoryPaths, (p) => Assert.True(WorkspaceService.IsPathInMemory(p)));

            string[] notInMemoryPaths = new[] {
                // Test short file absolute paths
                shortDirPath,
                shortFilePath,
                new Uri(shortDirPath).ToString(),
                new Uri(shortFilePath).ToString(),
                // Test short file relative paths
                "foo.ps1",
                Path.Combine(new[] { "..", "foo.ps1" })
            };

            Assert.All(notInMemoryPaths, (p) => Assert.False(WorkspaceService.IsPathInMemory(p)));
        }

        [Fact]
        public void CanOpenAndCloseFile()
        {
            WorkspaceService workspace = FixturesWorkspace();
            string filePath = Path.GetFullPath(Path.Combine(workspace.WorkspacePath, "rootfile.ps1"));

            ScriptFile file = workspace.GetFile(filePath);
            Assert.Equal(workspace.GetOpenedFiles(), new[] { file });

            workspace.CloseFile(file);
            Assert.Equal(workspace.GetOpenedFiles(), Array.Empty<ScriptFile>());
        }
    }
}
