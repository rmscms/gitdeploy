using System.Collections.Generic;
using System.Linq;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class TerminalSuggestionCatalog
    {
        public const string CategoryLaravel = "Laravel";
        public const string CategoryNavigation = "Navigation";
        public const string CategoryCustom = "Custom";

        public const string CategoryGitSetup = "Git Setup";
        public const string CategoryGitSnapshot = "Git Snapshot";
        public const string CategoryGitBranch = "Git Branch";
        public const string CategoryGitSync = "Git Sync";
        public const string CategoryGitHistory = "Git History";
        public const string CategoryGitStash = "Git Stash";
        public const string CategoryGitTag = "Git Tag";
        public const string CategoryGitUndo = "Git Undo";

        public const string CategoryComposerPackages = "Composer Packages";
        public const string CategoryComposerAutoload = "Composer Autoload";
        public const string CategoryComposerInspect = "Composer Inspect";
        public const string CategoryComposerProject = "Composer Project";
        public const string CategoryComposerScripts = "Composer Scripts";
        public const string CategoryComposerMaintain = "Composer Maintain";

        public static IReadOnlyList<string> GetKnownCategories()
        {
            return new[]
            {
                CategoryLaravel,
                CategoryNavigation,
                CategoryGitSetup,
                CategoryGitSnapshot,
                CategoryGitBranch,
                CategoryGitSync,
                CategoryGitHistory,
                CategoryGitStash,
                CategoryGitTag,
                CategoryGitUndo,
                CategoryComposerPackages,
                CategoryComposerAutoload,
                CategoryComposerInspect,
                CategoryComposerProject,
                CategoryComposerScripts,
                CategoryComposerMaintain,
                CategoryCustom
            };
        }

        public static IReadOnlyList<TerminalSuggestion> GetAllBuiltInDefaults()
        {
            return GetLaravelDefaults()
                .Concat(GetGitDefaults())
                .Concat(GetComposerDefaults())
                .ToList();
        }

        public static IReadOnlyList<TerminalSuggestion> GetLaravelDefaults()
        {
            return new List<TerminalSuggestion>
            {
                Entry("php artisan", "Artisan CLI entry point", CategoryLaravel),
                Entry("php artisan migrate", "Run database migrations", CategoryLaravel),
                Entry("php artisan migrate:status", "Show migration status", CategoryLaravel),
                Entry("php artisan migrate:rollback", "Rollback last migration batch", CategoryLaravel),
                Entry("php artisan migrate:fresh --seed", "Drop all tables and re-migrate with seed", CategoryLaravel),
                Entry("php artisan db:seed", "Run database seeders", CategoryLaravel),
                Entry("php artisan cache:clear", "Clear application cache", CategoryLaravel),
                Entry("php artisan config:clear", "Clear config cache", CategoryLaravel),
                Entry("php artisan config:cache", "Build config cache", CategoryLaravel),
                Entry("php artisan route:list", "List all routes", CategoryLaravel),
                Entry("php artisan route:cache", "Build route cache", CategoryLaravel),
                Entry("php artisan view:clear", "Clear compiled views", CategoryLaravel),
                Entry("php artisan optimize", "Cache config and routes", CategoryLaravel),
                Entry("php artisan optimize:clear", "Clear all optimization caches", CategoryLaravel),
                Entry("php artisan queue:work", "Process queue jobs", CategoryLaravel),
                Entry("php artisan queue:restart", "Restart queue workers", CategoryLaravel),
                Entry("php artisan storage:link", "Create storage symlink", CategoryLaravel),
                Entry("php artisan make:controller", "Create a controller class", CategoryLaravel),
                Entry("php artisan make:model", "Create an Eloquent model", CategoryLaravel),
                Entry("php artisan make:migration", "Create a migration file", CategoryLaravel),
                Entry("php artisan make:seeder", "Create a seeder class", CategoryLaravel),
                Entry("php artisan make:request", "Create a form request class", CategoryLaravel),
                Entry("php artisan make:middleware", "Create middleware class", CategoryLaravel),
                Entry("php artisan tinker", "Open REPL session", CategoryLaravel),
                Entry("php artisan serve", "Start development server", CategoryLaravel),
                Entry("php artisan schedule:run", "Run scheduled commands", CategoryLaravel),
                Entry("php artisan event:cache", "Cache events and listeners", CategoryLaravel),
                Entry("php artisan event:clear", "Clear event cache", CategoryLaravel),
                Entry("php artisan down", "Put application in maintenance mode", CategoryLaravel),
                Entry("php artisan up", "Bring application out of maintenance mode", CategoryLaravel),
            };
        }

        public static IReadOnlyList<TerminalSuggestion> GetGitDefaults()
        {
            return new List<TerminalSuggestion>
            {
                Entry("git", "Git CLI", CategoryGitSetup),
                Entry("git init", "Initialize a repository", CategoryGitSetup),
                Entry("git init -b main", "Init with main as default branch", CategoryGitSetup),
                Entry("git clone", "Clone a repository", CategoryGitSetup),
                Entry("git clone --depth 1", "Shallow clone", CategoryGitSetup),
                Entry("git config user.name", "Show/set user name", CategoryGitSetup),
                Entry("git config user.email", "Show/set user email", CategoryGitSetup),
                Entry("git config --global user.name", "Set global user name", CategoryGitSetup),
                Entry("git config --global user.email", "Set global user email", CategoryGitSetup),
                Entry("git config --list", "List config", CategoryGitSetup),
                Entry("git config --global --list", "List global config", CategoryGitSetup),

                Entry("git status", "Working tree status", CategoryGitSnapshot),
                Entry("git status -sb", "Short status with branch", CategoryGitSnapshot),
                Entry("git add .", "Stage all changes", CategoryGitSnapshot),
                Entry("git add -A", "Stage all including deletions", CategoryGitSnapshot),
                Entry("git add -p", "Stage hunks interactively", CategoryGitSnapshot),
                Entry("git add -u", "Stage tracked files only", CategoryGitSnapshot),
                Entry("git restore", "Restore working tree files", CategoryGitSnapshot),
                Entry("git restore --staged", "Unstage files", CategoryGitSnapshot),
                Entry("git restore --source", "Restore from a revision", CategoryGitSnapshot),
                Entry("git diff", "Unstaged diff", CategoryGitSnapshot),
                Entry("git diff --staged", "Staged diff", CategoryGitSnapshot),
                Entry("git diff HEAD", "Diff vs HEAD", CategoryGitSnapshot),
                Entry("git commit -m", "Commit with message", CategoryGitSnapshot),
                Entry("git commit -am", "Stage tracked and commit", CategoryGitSnapshot),
                Entry("git commit --amend", "Amend last commit", CategoryGitSnapshot),
                Entry("git commit --amend --no-edit", "Amend without editing message", CategoryGitSnapshot),
                Entry("git rm", "Remove file from index and disk", CategoryGitSnapshot),
                Entry("git mv", "Move/rename file", CategoryGitSnapshot),

                Entry("git branch", "List branches", CategoryGitBranch),
                Entry("git branch -a", "List local and remote branches", CategoryGitBranch),
                Entry("git branch -d", "Delete merged branch", CategoryGitBranch),
                Entry("git branch -D", "Force-delete branch", CategoryGitBranch),
                Entry("git branch -m", "Rename current branch", CategoryGitBranch),
                Entry("git checkout", "Switch branch or restore files", CategoryGitBranch),
                Entry("git checkout -b", "Create and switch branch", CategoryGitBranch),
                Entry("git switch", "Switch branch", CategoryGitBranch),
                Entry("git switch -c", "Create and switch branch", CategoryGitBranch),
                Entry("git merge", "Merge branch", CategoryGitBranch),
                Entry("git merge --abort", "Abort merge", CategoryGitBranch),
                Entry("git merge --no-ff", "Merge with merge commit", CategoryGitBranch),
                Entry("git rebase", "Rebase onto branch", CategoryGitBranch),
                Entry("git rebase -i", "Interactive rebase", CategoryGitBranch),
                Entry("git rebase --continue", "Continue rebase", CategoryGitBranch),
                Entry("git rebase --abort", "Abort rebase", CategoryGitBranch),
                Entry("git rebase --skip", "Skip rebase commit", CategoryGitBranch),
                Entry("git cherry-pick", "Apply a commit", CategoryGitBranch),
                Entry("git cherry-pick --abort", "Abort cherry-pick", CategoryGitBranch),
                Entry("git cherry-pick --continue", "Continue cherry-pick", CategoryGitBranch),

                Entry("git remote -v", "List remotes", CategoryGitSync),
                Entry("git remote add origin", "Add origin remote", CategoryGitSync),
                Entry("git remote set-url origin", "Change origin URL", CategoryGitSync),
                Entry("git remote remove", "Remove a remote", CategoryGitSync),
                Entry("git fetch", "Fetch remotes", CategoryGitSync),
                Entry("git fetch --all --prune", "Fetch all and prune", CategoryGitSync),
                Entry("git pull", "Fetch and merge", CategoryGitSync),
                Entry("git pull --rebase", "Fetch and rebase", CategoryGitSync),
                Entry("git pull origin", "Pull from origin", CategoryGitSync),
                Entry("git push", "Push current branch", CategoryGitSync),
                Entry("git push -u origin HEAD", "Push and set upstream", CategoryGitSync),
                Entry("git push --force-with-lease", "Safer force push", CategoryGitSync),
                Entry("git push --all", "Push all branches", CategoryGitSync),
                Entry("git push --tags", "Push tags", CategoryGitSync),
                Entry("git push origin --delete", "Delete remote branch", CategoryGitSync),

                Entry("git log", "Commit history", CategoryGitHistory),
                Entry("git log --oneline", "Compact history", CategoryGitHistory),
                Entry("git log --oneline --graph --decorate --all", "Graph of all branches", CategoryGitHistory),
                Entry("git log -p", "History with patches", CategoryGitHistory),
                Entry("git log -n 20", "Last 20 commits", CategoryGitHistory),
                Entry("git show", "Show commit or object", CategoryGitHistory),
                Entry("git show HEAD", "Show latest commit", CategoryGitHistory),
                Entry("git blame", "Line-by-line authorship", CategoryGitHistory),
                Entry("git reflog", "Reference log", CategoryGitHistory),
                Entry("git shortlog -sn", "Commit counts by author", CategoryGitHistory),

                Entry("git stash", "Stash working changes", CategoryGitStash),
                Entry("git stash push -m", "Stash with message", CategoryGitStash),
                Entry("git stash -u", "Stash including untracked", CategoryGitStash),
                Entry("git stash list", "List stashes", CategoryGitStash),
                Entry("git stash show", "Show stash diff", CategoryGitStash),
                Entry("git stash pop", "Apply and drop stash", CategoryGitStash),
                Entry("git stash apply", "Apply stash keep entry", CategoryGitStash),
                Entry("git stash drop", "Drop a stash", CategoryGitStash),
                Entry("git stash clear", "Drop all stashes", CategoryGitStash),

                Entry("git tag", "List tags", CategoryGitTag),
                Entry("git tag -a", "Annotated tag", CategoryGitTag),
                Entry("git tag -d", "Delete local tag", CategoryGitTag),

                Entry("git reset", "Reset current HEAD", CategoryGitUndo),
                Entry("git reset --soft HEAD~1", "Undo commit keep index", CategoryGitUndo),
                Entry("git reset --mixed HEAD~1", "Undo commit unstage", CategoryGitUndo),
                Entry("git reset --hard HEAD~1", "Undo commit discard files", CategoryGitUndo),
                Entry("git reset --hard origin/main", "Match origin/main", CategoryGitUndo),
                Entry("git revert", "Revert a commit", CategoryGitUndo),
                Entry("git revert --no-edit", "Revert without editor", CategoryGitUndo),
                Entry("git clean -fd", "Remove untracked files and dirs", CategoryGitUndo),
                Entry("git clean -fdn", "Dry-run clean", CategoryGitUndo),
            };
        }

        public static IReadOnlyList<TerminalSuggestion> GetComposerDefaults()
        {
            return new List<TerminalSuggestion>
            {
                Entry("composer", "Composer CLI", CategoryComposerPackages),
                Entry("composer install", "Install from lock file", CategoryComposerPackages),
                Entry("composer install --no-dev", "Install without dev deps", CategoryComposerPackages),
                Entry("composer install --prefer-dist", "Prefer dist packages", CategoryComposerPackages),
                Entry("composer update", "Update dependencies", CategoryComposerPackages),
                Entry("composer update --with-dependencies", "Update with transitive deps", CategoryComposerPackages),
                Entry("composer update --lock", "Update lock hash only", CategoryComposerPackages),
                Entry("composer require", "Add a package", CategoryComposerPackages),
                Entry("composer require --dev", "Add a dev package", CategoryComposerPackages),
                Entry("composer remove", "Remove a package", CategoryComposerPackages),
                Entry("composer remove --dev", "Remove a dev package", CategoryComposerPackages),
                Entry("composer reinstall", "Reinstall a package", CategoryComposerPackages),
                Entry("composer bump", "Bump version constraints", CategoryComposerPackages),
                Entry("composer bump --dev-only", "Bump dev constraints", CategoryComposerPackages),

                Entry("composer dump-autoload", "Rebuild autoloader", CategoryComposerAutoload),
                Entry("composer dump-autoload -o", "Optimized autoloader", CategoryComposerAutoload),
                Entry("composer dump-autoload -a", "Authoritative classmap", CategoryComposerAutoload),
                Entry("composer dump-autoload --no-dev", "Autoload without dev", CategoryComposerAutoload),

                Entry("composer show", "List installed packages", CategoryComposerInspect),
                Entry("composer show -i", "Installed packages only", CategoryComposerInspect),
                Entry("composer outdated", "Outdated packages", CategoryComposerInspect),
                Entry("composer outdated --direct", "Direct deps only", CategoryComposerInspect),
                Entry("composer why", "Why a package is installed", CategoryComposerInspect),
                Entry("composer why-not", "Why a version cannot install", CategoryComposerInspect),
                Entry("composer depends", "Package dependents", CategoryComposerInspect),
                Entry("composer prohibits", "What blocks a version", CategoryComposerInspect),
                Entry("composer audit", "Security audit", CategoryComposerInspect),
                Entry("composer licenses", "Package licenses", CategoryComposerInspect),
                Entry("composer suggest", "Suggested packages", CategoryComposerInspect),
                Entry("composer status", "Local modifications vs dist", CategoryComposerInspect),
                Entry("composer fund", "Funding info", CategoryComposerInspect),

                Entry("composer init", "Create composer.json", CategoryComposerProject),
                Entry("composer create-project", "Create project from package", CategoryComposerProject),
                Entry("composer create-project laravel/laravel", "New Laravel app", CategoryComposerProject),
                Entry("composer validate", "Validate composer.json/lock", CategoryComposerProject),
                Entry("composer check-platform-reqs", "Check PHP/ext requirements", CategoryComposerProject),
                Entry("composer search", "Search Packagist", CategoryComposerProject),

                Entry("composer run-script", "Run a composer script", CategoryComposerScripts),
                Entry("composer run", "Alias for run-script", CategoryComposerScripts),
                Entry("composer run-script --list", "List defined scripts", CategoryComposerScripts),
                Entry("composer exec", "Run a vendor binary", CategoryComposerScripts),
                Entry("composer test", "Common test script", CategoryComposerScripts),

                Entry("composer clear-cache", "Clear Composer cache", CategoryComposerMaintain),
                Entry("composer diagnose", "Diagnose common problems", CategoryComposerMaintain),
                Entry("composer self-update", "Update Composer itself", CategoryComposerMaintain),
                Entry("composer self-update --rollback", "Rollback Composer phar", CategoryComposerMaintain),
                Entry("composer config --list", "List config", CategoryComposerMaintain),
                Entry("composer config --global --list", "Global config", CategoryComposerMaintain),
                Entry("composer about", "Composer about", CategoryComposerMaintain),
                Entry("composer help", "Help", CategoryComposerMaintain),
                Entry("composer list", "List commands", CategoryComposerMaintain),
                Entry("composer global require", "Global package", CategoryComposerMaintain),
                Entry("composer global update", "Update global packages", CategoryComposerMaintain),
                Entry("composer archive", "Archive the project", CategoryComposerMaintain),
            };
        }

        private static TerminalSuggestion Entry(string command, string description, string category)
        {
            return new TerminalSuggestion
            {
                Command = command,
                Description = description,
                Category = category,
                Scope = TerminalSuggestionScope.Global,
                IsEnabled = true
            };
        }
    }
}
