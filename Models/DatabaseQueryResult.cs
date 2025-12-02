using System.Data;

namespace GitDeployPro.Models
{
    public class DatabaseQueryResult
    {
        public bool HasResultSet { get; set; }
        public DataTable? Table { get; set; }
        public int RowsAffected { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}

