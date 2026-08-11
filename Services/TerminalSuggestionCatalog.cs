using System.Collections.Generic;
using GitDeployPro.Models;

namespace GitDeployPro.Services
{
    public static class TerminalSuggestionCatalog
    {
        public const string CategoryLaravel = "Laravel";
        public const string CategoryNavigation = "Navigation";
        public const string CategoryCustom = "Custom";

        public static IReadOnlyList<TerminalSuggestion> GetLaravelDefaults()
        {
            return new List<TerminalSuggestion>
            {
                Entry("php artisan", "Artisan CLI entry point"),
                Entry("php artisan migrate", "Run database migrations"),
                Entry("php artisan migrate:status", "Show migration status"),
                Entry("php artisan migrate:rollback", "Rollback last migration batch"),
                Entry("php artisan migrate:fresh --seed", "Drop all tables and re-migrate with seed"),
                Entry("php artisan db:seed", "Run database seeders"),
                Entry("php artisan cache:clear", "Clear application cache"),
                Entry("php artisan config:clear", "Clear config cache"),
                Entry("php artisan config:cache", "Build config cache"),
                Entry("php artisan route:list", "List all routes"),
                Entry("php artisan route:cache", "Build route cache"),
                Entry("php artisan view:clear", "Clear compiled views"),
                Entry("php artisan optimize", "Cache config and routes"),
                Entry("php artisan optimize:clear", "Clear all optimization caches"),
                Entry("php artisan queue:work", "Process queue jobs"),
                Entry("php artisan queue:restart", "Restart queue workers"),
                Entry("php artisan storage:link", "Create storage symlink"),
                Entry("php artisan make:controller", "Create a controller class"),
                Entry("php artisan make:model", "Create an Eloquent model"),
                Entry("php artisan make:migration", "Create a migration file"),
                Entry("php artisan make:seeder", "Create a seeder class"),
                Entry("php artisan make:request", "Create a form request class"),
                Entry("php artisan make:middleware", "Create middleware class"),
                Entry("php artisan tinker", "Open REPL session"),
                Entry("php artisan serve", "Start development server"),
                Entry("php artisan schedule:run", "Run scheduled commands"),
                Entry("php artisan event:cache", "Cache events and listeners"),
                Entry("php artisan event:clear", "Clear event cache"),
                Entry("php artisan down", "Put application in maintenance mode"),
                Entry("php artisan up", "Bring application out of maintenance mode"),
            };
        }

        private static TerminalSuggestion Entry(string command, string description)
        {
            return new TerminalSuggestion
            {
                Command = command,
                Description = description,
                Category = CategoryLaravel,
                Scope = TerminalSuggestionScope.Global,
                IsEnabled = true
            };
        }
    }
}
