using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using GitDeployPro.Controls;
using GitDeployPro.Models;
using GitDeployPro.Services;
using GitDeployPro.Windows;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace GitDeployPro.Pages
{
    public partial class DatabasePage : Page
    {
        private readonly ConfigurationService _configService = new();
        private readonly ObservableCollection<string> _databaseOptions = new();
        private readonly ObservableCollection<string> _tables = new();
        private readonly List<string> _tableCache = new();
        private readonly DatabaseClient _client = new();
        private readonly List<string> _columnCache = new();
        private CompletionWindow? _completionWindow;

        private DatabaseConnectionEntry? _activeConnection;
        private bool _isInitialized;
        private bool _sidebarAdjusted;
        private bool _collapsedSidebarByPage;

        // SQL Keywords for autocomplete
        private static readonly string[] SqlKeywords = {
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "LIKE", "BETWEEN", "IS", "NULL",
            "AS", "ON", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL", "UNION", "ALL",
            "DISTINCT", "ORDER", "BY", "ASC", "DESC", "GROUP", "HAVING", "LIMIT", "OFFSET",
            "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP",
            "TABLE", "INDEX", "VIEW", "DATABASE", "IF", "EXISTS", "PRIMARY", "KEY", "FOREIGN",
            "REFERENCES", "UNIQUE", "DEFAULT", "AUTO_INCREMENT", "CONSTRAINT", "CASCADE",
            "TRUNCATE", "EXPLAIN", "SHOW", "DESCRIBE", "USE", "BEGIN", "COMMIT", "ROLLBACK",
            "TRANSACTION", "CASE", "WHEN", "THEN", "ELSE", "END", "TRUE", "FALSE"
        };

        private static readonly string[] SqlFunctions = {
            "COUNT", "SUM", "AVG", "MIN", "MAX", "CONCAT", "SUBSTRING", "LENGTH", "UPPER", "LOWER",
            "TRIM", "LTRIM", "RTRIM", "REPLACE", "COALESCE", "IFNULL", "NULLIF", "CAST", "CONVERT",
            "NOW", "CURDATE", "CURTIME", "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
            "DATEDIFF", "DATE_ADD", "DATE_SUB", "DATE_FORMAT", "ROUND", "FLOOR", "CEIL", "ABS",
            "MOD", "RAND", "GROUP_CONCAT", "FIND_IN_SET", "UUID"
        };

        public DatabasePage()
        {
            InitializeComponent();
            Loaded += DatabasePage_Loaded;
            Unloaded += DatabasePage_Unloaded;

            DatabaseSelector.ItemsSource = _databaseOptions;
            TableSelector.ItemsSource = _tables;

            InitializeSqlEditor();
        }

        private void InitializeSqlEditor()
        {
            SqlEditor.SyntaxHighlighting = CreateSqlHighlighting();
            SqlEditor.TextArea.TextEntering += SqlEditor_TextEntering;
            SqlEditor.TextArea.TextEntered += SqlEditor_TextEntered;
            SqlEditor.PreviewKeyDown += SqlEditor_PreviewKeyDown;
        }

        private IHighlightingDefinition CreateSqlHighlighting()
        {
            var xshdXml = @"<?xml version=""1.0""?>
<SyntaxDefinition name=""SQL"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#6A9955"" fontStyle=""italic""/>
  <Color name=""String"" foreground=""#CE9178""/>
  <Color name=""Keyword"" foreground=""#569CD6"" fontWeight=""bold""/>
  <Color name=""Function"" foreground=""#DCDCAA""/>
  <Color name=""Number"" foreground=""#B5CEA8""/>
  <Color name=""DataType"" foreground=""#4EC9B0""/>
  
  <RuleSet ignoreCase=""true"">
    <Span color=""Comment"" begin=""--"" />
    <Span color=""Comment"" multiline=""true"" begin=""/\*"" end=""\*/"" />
    <Span color=""String"" begin=""'"" end=""'"" />
    <Span color=""String"" begin=""&quot;"" end=""&quot;"" />
    
    <Rule color=""Number"">
      \b\d+(\.\d+)?\b
    </Rule>
    
    <Keywords color=""Keyword"">
      <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>AND</Word><Word>OR</Word>
      <Word>NOT</Word><Word>IN</Word><Word>LIKE</Word><Word>BETWEEN</Word><Word>IS</Word>
      <Word>NULL</Word><Word>AS</Word><Word>ON</Word><Word>JOIN</Word><Word>LEFT</Word>
      <Word>RIGHT</Word><Word>INNER</Word><Word>OUTER</Word><Word>CROSS</Word><Word>FULL</Word>
      <Word>UNION</Word><Word>ALL</Word><Word>DISTINCT</Word><Word>ORDER</Word><Word>BY</Word>
      <Word>ASC</Word><Word>DESC</Word><Word>GROUP</Word><Word>HAVING</Word><Word>LIMIT</Word>
      <Word>OFFSET</Word><Word>INSERT</Word><Word>INTO</Word><Word>VALUES</Word><Word>UPDATE</Word>
      <Word>SET</Word><Word>DELETE</Word><Word>CREATE</Word><Word>ALTER</Word><Word>DROP</Word>
      <Word>TABLE</Word><Word>INDEX</Word><Word>VIEW</Word><Word>DATABASE</Word><Word>IF</Word>
      <Word>EXISTS</Word><Word>PRIMARY</Word><Word>KEY</Word><Word>FOREIGN</Word><Word>REFERENCES</Word>
      <Word>UNIQUE</Word><Word>DEFAULT</Word><Word>AUTO_INCREMENT</Word><Word>CONSTRAINT</Word>
      <Word>CASCADE</Word><Word>TRUNCATE</Word><Word>EXPLAIN</Word><Word>SHOW</Word>
      <Word>DESCRIBE</Word><Word>USE</Word><Word>BEGIN</Word><Word>COMMIT</Word><Word>ROLLBACK</Word>
      <Word>TRANSACTION</Word><Word>CASE</Word><Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word>
      <Word>END</Word><Word>TRUE</Word><Word>FALSE</Word>
    </Keywords>
    
    <Keywords color=""Function"">
      <Word>COUNT</Word><Word>SUM</Word><Word>AVG</Word><Word>MIN</Word><Word>MAX</Word>
      <Word>CONCAT</Word><Word>SUBSTRING</Word><Word>LENGTH</Word><Word>UPPER</Word><Word>LOWER</Word>
      <Word>TRIM</Word><Word>REPLACE</Word><Word>COALESCE</Word><Word>IFNULL</Word><Word>NULLIF</Word>
      <Word>CAST</Word><Word>CONVERT</Word><Word>NOW</Word><Word>CURDATE</Word><Word>YEAR</Word>
      <Word>MONTH</Word><Word>DAY</Word><Word>DATEDIFF</Word><Word>DATE_FORMAT</Word><Word>ROUND</Word>
      <Word>FLOOR</Word><Word>CEIL</Word><Word>ABS</Word><Word>MOD</Word><Word>RAND</Word>
      <Word>GROUP_CONCAT</Word><Word>UUID</Word>
    </Keywords>
    
    <Keywords color=""DataType"">
      <Word>INT</Word><Word>INTEGER</Word><Word>BIGINT</Word><Word>SMALLINT</Word><Word>TINYINT</Word>
      <Word>FLOAT</Word><Word>DOUBLE</Word><Word>DECIMAL</Word><Word>CHAR</Word><Word>VARCHAR</Word>
      <Word>TEXT</Word><Word>BLOB</Word><Word>DATE</Word><Word>DATETIME</Word><Word>TIMESTAMP</Word>
      <Word>TIME</Word><Word>BOOLEAN</Word><Word>BOOL</Word><Word>ENUM</Word><Word>JSON</Word>
      <Word>UNSIGNED</Word><Word>SIGNED</Word>
    </Keywords>
  </RuleSet>
</SyntaxDefinition>";

            using var reader = new XmlTextReader(new StringReader(xshdXml));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }

        private void SqlEditor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ShowCompletionWindow();
                e.Handled = true;
            }
        }

        private void SqlEditor_TextEntering(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length > 0 && _completionWindow != null)
            {
                if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
                {
                    _completionWindow.CompletionList.RequestInsertion(e);
                }
            }
        }

        private void SqlEditor_TextEntered(object sender, TextCompositionEventArgs e)
        {
            if (e.Text.Length == 1 && char.IsLetter(e.Text[0]))
            {
                ShowCompletionWindow();
            }
            else if (e.Text == ".")
            {
                ShowCompletionWindow();
            }
        }

        private void ShowCompletionWindow()
        {
            if (_completionWindow != null) return;

            var completionData = GetCompletionData();
            if (!completionData.Any()) return;

            _completionWindow = new CompletionWindow(SqlEditor.TextArea);
            _completionWindow.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
            _completionWindow.Foreground = System.Windows.Media.Brushes.White;
            _completionWindow.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3F3F46"));
            _completionWindow.BorderThickness = new Thickness(1);
            _completionWindow.CompletionList.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526"));
            _completionWindow.CompletionList.Foreground = System.Windows.Media.Brushes.White;

            var data = _completionWindow.CompletionList.CompletionData;
            foreach (var item in completionData)
            {
                data.Add(item);
            }

            _completionWindow.Show();
            _completionWindow.Closed += (s, args) => _completionWindow = null;
        }

        private IEnumerable<ICompletionData> GetCompletionData()
        {
            var result = new List<SqlCompletionData>();
            var currentWord = GetCurrentWord().ToUpper();

            foreach (var keyword in SqlKeywords.Where(k => k.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new SqlCompletionData(keyword, "SQL Keyword", "🔑"));
            }

            foreach (var func in SqlFunctions.Where(f => f.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new SqlCompletionData(func + "()", "SQL Function", "ƒ"));
            }

            foreach (var table in _tableCache.Where(t => t.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new SqlCompletionData(table, "Table", "📋"));
            }

            foreach (var col in _columnCache.Where(c => c.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new SqlCompletionData(col, "Column", "📊"));
            }

            return result.OrderBy(x => x.Text);
        }

        private string GetCurrentWord()
        {
            var offset = SqlEditor.CaretOffset;
            var doc = SqlEditor.Document;

            if (offset == 0) return string.Empty;

            var start = offset - 1;
            while (start >= 0 && (char.IsLetterOrDigit(doc.GetCharAt(start)) || doc.GetCharAt(start) == '_'))
            {
                start--;
            }
            start++;

            return doc.GetText(start, offset - start);
        }

        private void DatabasePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_sidebarAdjusted)
            {
                UpdateSidebarState(true);
                _sidebarAdjusted = true;
            }
            if (_isInitialized) return;
            _isInitialized = true;
            UpdateSavedConnectionsCount();
        }

        private async void DatabasePage_Unloaded(object sender, RoutedEventArgs e)
        {
            await _client.DisconnectAsync();
            UpdateSidebarState(false);
            _sidebarAdjusted = false;
        }

        private void UpdateSavedConnectionsCount()
        {
            var profiles = _configService.LoadConnections();
            var dbProfiles = profiles.Count(p => p.DbType != DatabaseType.None);
            SavedCountBadge.Text = $" ({dbProfiles + 1})"; // +1 for localhost
        }

        // ========== NEW TOOLBAR BUTTON HANDLERS ==========

        private void NewConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new NewConnectionWindow { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.ResultConnection != null)
            {
                _ = ConnectToEntryAsync(window.ResultConnection);
            }
        }

        private void SavedConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new SavedConnectionsWindow { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.ShouldConnect && window.SelectedConnection != null)
            {
                _ = ConnectToEntryAsync(window.SelectedConnection);
            }
            UpdateSavedConnectionsCount();
        }

        private async void LocalhostButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = DatabaseConnectionEntry.CreateLocalDefault();
            await ConnectToEntryAsync(entry);
        }

        // ========== CONNECTION LOGIC ==========

        private async Task ConnectToEntryAsync(DatabaseConnectionEntry entry)
        {
            if (!entry.SupportsCurrentVersion)
            {
                ModernMessageBox.Show("This build supports MySQL/MariaDB only. Others coming soon.", "Not Supported", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                SetBusy(true, $"Connecting to {entry.Name}...");

                await _client.ConnectAsync(entry.ToConnectionInfo());
                _activeConnection = entry;

                // Update UI
                StatusIndicator.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4CAF50"));
                ConnectionStatusText.Text = $"Connected to {entry.Host}:{entry.Port}";
                ConnectionStatusText.Foreground = System.Windows.Media.Brushes.White;
                ActiveDatabaseText.Text = "";
                RefreshButton.IsEnabled = true;
                DisconnectButton.IsEnabled = true;
                SelectorBar.Visibility = Visibility.Visible;
                DisconnectedState.Visibility = Visibility.Collapsed;
                DatabaseContent.Visibility = Visibility.Visible;

                await LoadSchemaAsync(entry);
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Failed to connect: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ShowDisconnectedState();
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LoadSchemaAsync(DatabaseConnectionEntry entry)
        {
            try
            {
                SetBusy(true, "Loading databases...");
                _databaseOptions.Clear();

                var databases = await _client.GetDatabasesAsync();
                foreach (var db in databases)
                {
                    _databaseOptions.Add(db);
                }

                string? targetDb = null;
                if (!string.IsNullOrWhiteSpace(entry.DatabaseName) && databases.Contains(entry.DatabaseName))
                {
                    targetDb = entry.DatabaseName;
                }
                else if (databases.Any())
                {
                    targetDb = databases.First();
                }

                if (!string.IsNullOrWhiteSpace(targetDb))
                {
                    DatabaseSelector.SelectedItem = targetDb;
                    ActiveDatabaseText.Text = $"· {targetDb}";
                }
                else
                {
                    DatabaseSelector.SelectedItem = null;
                    _tables.Clear();
                    _tableCache.Clear();
                }
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load schema: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task LoadTablesAsync(string database)
        {
            try
            {
                SetBusy(true, "Fetching tables...");
                var tables = await _client.GetTablesAsync(database);

                _tableCache.Clear();
                _tableCache.AddRange(tables);
                ApplyTableFilter();
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load tables: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshSchemaButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConnection == null) return;
            await LoadSchemaAsync(_activeConnection);
        }

        private async void DatabaseSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DatabaseSelector.SelectedItem is string db && _activeConnection != null)
            {
                ActiveDatabaseText.Text = $"· {db}";
                await LoadTablesAsync(db);
            }
            else
            {
                ActiveDatabaseText.Text = "";
                _tables.Clear();
                _tableCache.Clear();
            }
        }

        private void ApplyTableFilter()
        {
            _tables.Clear();
            foreach (var table in _tableCache)
            {
                _tables.Add(table);
            }
        }

        private async void TableSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TableSelector.SelectedItem is string table && DatabaseSelector.SelectedItem is string db)
            {
                await LoadTablePreviewAsync(db, table);
            }
        }

        private async Task LoadTablePreviewAsync(string database, string table)
        {
            try
            {
                SetQueryRunning(true);
                var preview = await _client.GetTablePreviewAsync(database, table);

                ResultsGrid.ItemsSource = preview.Table?.DefaultView;
                TableTitleText.Text = $"Table Preview · {table}";
                ResultStatusText.Text = preview.Message;

                _columnCache.Clear();
                if (preview.Table != null)
                {
                    foreach (System.Data.DataColumn col in preview.Table.Columns)
                    {
                        _columnCache.Add(col.ColumnName);
                    }
                }

                SqlEditor.Text = $"SELECT * FROM `{table}` LIMIT 100;";
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Unable to load table: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetQueryRunning(false);
            }
        }

        private async void RunQueryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_client.IsConnected)
            {
                ModernMessageBox.Show("Connect to a database first.", "No Connection", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sql = SqlEditor.Text?.Trim();
            if (string.IsNullOrWhiteSpace(sql))
            {
                ModernMessageBox.Show("Please enter a SQL query.", "Empty Query", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var database = DatabaseSelector.SelectedItem as string;
            try
            {
                SetQueryRunning(true);
                var result = await _client.ExecuteQueryAsync(sql, database);

                if (result.HasResultSet && result.Table != null)
                {
                    ResultsGrid.ItemsSource = result.Table.DefaultView;
                    TableTitleText.Text = "Query Result";
                }
                else
                {
                    ResultsGrid.ItemsSource = null;
                }

                ResultStatusText.Text = result.Message;
            }
            catch (Exception ex)
            {
                ModernMessageBox.Show($"Query failed: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetQueryRunning(false);
            }
        }

        private void ClearQueryButton_Click(object sender, RoutedEventArgs e)
        {
            SqlEditor.Clear();
            ResultStatusText.Text = "Query editor cleared.";
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await _client.DisconnectAsync();
            ShowDisconnectedState();
        }

        private void ShowDisconnectedState()
        {
            _activeConnection = null;
            RefreshButton.IsEnabled = false;
            DisconnectButton.IsEnabled = false;
            StatusIndicator.Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#666666"));
            ConnectionStatusText.Text = "Not Connected";
            ConnectionStatusText.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A0A0A0"));
            ActiveDatabaseText.Text = "";
            SelectorBar.Visibility = Visibility.Collapsed;
            DatabaseContent.Visibility = Visibility.Collapsed;
            DisconnectedState.Visibility = Visibility.Visible;
            ResultsGrid.ItemsSource = null;
            _databaseOptions.Clear();
            _tables.Clear();
            _tableCache.Clear();
        }

        private void SetBusy(bool isBusy, string? message = null)
        {
            LoadingOverlay.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(message))
            {
                LoadingText.Text = message;
            }
        }

        private void SetQueryRunning(bool isRunning)
        {
            QueryProgress.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
            RunQueryButton.IsEnabled = !isRunning;
            ClearQueryButton.IsEnabled = !isRunning;
            TableSelector.IsEnabled = !isRunning;
        }

        private void OpenConnectionManager_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ConnectionManagerWindow { Owner = Window.GetWindow(this) };
            manager.ShowDialog();
            UpdateSavedConnectionsCount();
        }

        private void UpdateSidebarState(bool collapse)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                if (collapse)
                {
                    if (!mainWindow.IsSidebarCollapsed)
                    {
                        mainWindow.SetSidebarCollapsed(true);
                        _collapsedSidebarByPage = true;
                    }
                }
                else if (_collapsedSidebarByPage)
                {
                    mainWindow.SetSidebarCollapsed(false);
                    _collapsedSidebarByPage = false;
                }
            }
        }

        // ========== INNER CLASSES ==========

        private class SqlCompletionData : ICompletionData
        {
            public SqlCompletionData(string text, string description, string icon)
            {
                Text = text;
                Description = $"{icon} {description}";
                _icon = icon;
            }

            private readonly string _icon;

            public System.Windows.Media.ImageSource? Image => null;
            public string Text { get; }
            public object Content => $"{_icon} {Text}";
            public object Description { get; }
            public double Priority => 0;

            public void Complete(ICSharpCode.AvalonEdit.Editing.TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            {
                var doc = textArea.Document;
                var offset = completionSegment.Offset;
                var start = offset;

                while (start > 0 && (char.IsLetterOrDigit(doc.GetCharAt(start - 1)) || doc.GetCharAt(start - 1) == '_'))
                {
                    start--;
                }

                var length = completionSegment.EndOffset - start;
                var insertText = Text;

                if (insertText.EndsWith("()"))
                {
                    doc.Replace(start, length, insertText);
                    textArea.Caret.Offset = start + insertText.Length - 1;
                }
                else
                {
                    doc.Replace(start, length, insertText);
                }
            }
        }
    }
}
