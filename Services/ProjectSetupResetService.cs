using System;
using System.Collections.Generic;
using System.IO;

namespace GitDeployPro.Services
{
    public sealed class ProjectSetupResetService
    {
        private const string ProjectConfigFileName = ".gitdeploy.config";

        public ProjectSetupResetPreview BuildPreview(string projectPath, bool removeGitFolder, bool removeProjectConfig)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("Project path is required.", nameof(projectPath));
            }

            var normalizedProjectPath = Path.GetFullPath(projectPath);
            var targets = new List<string>();

            if (removeGitFolder)
            {
                targets.Add(Path.Combine(normalizedProjectPath, ".git"));
            }

            if (removeProjectConfig)
            {
                targets.Add(Path.Combine(normalizedProjectPath, ProjectConfigFileName));
            }

            return new ProjectSetupResetPreview(normalizedProjectPath, targets);
        }

        public ProjectSetupResetResult ResetProject(string projectPath, bool removeGitFolder, bool removeProjectConfig)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("Project path is required.", nameof(projectPath));
            }

            var normalizedProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(normalizedProjectPath))
            {
                throw new DirectoryNotFoundException($"Project path does not exist: {normalizedProjectPath}");
            }

            var preview = BuildPreview(normalizedProjectPath, removeGitFolder, removeProjectConfig);
            var removed = new List<string>();
            var skipped = new List<string>();

            foreach (var target in preview.Targets)
            {
                if (target.EndsWith(Path.DirectorySeparatorChar + ".git", StringComparison.OrdinalIgnoreCase) ||
                    target.EndsWith("/.git", StringComparison.OrdinalIgnoreCase) ||
                    target.EndsWith("\\.git", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(target))
                    {
                        skipped.Add(target);
                        continue;
                    }

                    PrepareDirectoryForDeletion(target);
                    Directory.Delete(target, recursive: true);
                    removed.Add(target);
                    continue;
                }

                if (!File.Exists(target))
                {
                    skipped.Add(target);
                    continue;
                }

                UnhideAndUnlockFile(target);
                File.Delete(target);
                removed.Add(target);
            }

            return new ProjectSetupResetResult(removed, skipped);
        }

        private static void PrepareDirectoryForDeletion(string directoryPath)
        {
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                UnhideAndUnlockFile(file);
            }

            foreach (var directory in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
            {
                UnhideAndUnlockDirectory(directory);
            }

            UnhideAndUnlockDirectory(directoryPath);
        }

        private static void UnhideAndUnlockFile(string filePath)
        {
            var attributes = File.GetAttributes(filePath);
            attributes &= ~FileAttributes.ReadOnly;
            attributes &= ~FileAttributes.Hidden;
            attributes &= ~FileAttributes.System;
            File.SetAttributes(filePath, attributes);
        }

        private static void UnhideAndUnlockDirectory(string directoryPath)
        {
            var attributes = File.GetAttributes(directoryPath);
            attributes &= ~FileAttributes.ReadOnly;
            attributes &= ~FileAttributes.Hidden;
            attributes &= ~FileAttributes.System;
            File.SetAttributes(directoryPath, attributes);
        }
    }

    public sealed class ProjectSetupResetPreview
    {
        public ProjectSetupResetPreview(string projectPath, IReadOnlyList<string> targets)
        {
            ProjectPath = projectPath;
            Targets = targets;
        }

        public string ProjectPath { get; }
        public IReadOnlyList<string> Targets { get; }
    }

    public sealed class ProjectSetupResetResult
    {
        public ProjectSetupResetResult(IReadOnlyList<string> removedPaths, IReadOnlyList<string> skippedPaths)
        {
            RemovedPaths = removedPaths;
            SkippedPaths = skippedPaths;
        }

        public IReadOnlyList<string> RemovedPaths { get; }
        public IReadOnlyList<string> SkippedPaths { get; }
    }
}
