using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;

namespace StartDock.Services
{
    /// <summary>
    /// File search for the dock's search box (AppConfig.SearchFiles), using Windows'
    /// own search index — the same one File Explorer's search box and the real Start
    /// Menu use — so it's answering from a prebuilt index rather than walking the disk.
    /// That's what keeps it fast; MainWindow also runs it on a background thread and
    /// only after typing pauses, so the dock never waits on it.
    ///
    /// Only finds what Windows has indexed (by default: the user's own folders —
    /// Desktop, Documents, Downloads, Pictures and so on). Locations can be added in
    /// Windows Settings → Privacy &amp; security → Searching Windows.
    /// </summary>
    public static class FileSearchService
    {
        public readonly record struct FileResult(string Name, string Path);

        private const string ConnectionString =
            "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

        /// <summary>Files whose name contains <paramref name="term"/>, most recently
        /// modified first. Returns an empty list if the index isn't available (the
        /// Windows Search service turned off, say). Call off the UI thread.</summary>
        public static List<FileResult> Search(string term, int max)
        {
            var results = new List<FileResult>();
            if (string.IsNullOrWhiteSpace(term))
                return results;

            // The query language uses single-quoted strings: double any single quote.
            // Square brackets would be read as LIKE character classes — drop them.
            string safe = term.Replace("'", "''").Replace("[", "").Replace("]", "");

            string sql =
                $"SELECT TOP {max} System.ItemPathDisplay " +
                "FROM SystemIndex " +
                "WHERE SCOPE='file:' " +
                "AND System.ItemType <> 'Directory' " +
                $"AND System.FileName LIKE '%{safe}%' " +
                // App data, caches and the like — not what anyone means by "my files".
                "AND NOT System.ItemPathDisplay LIKE '%\\AppData\\%' " +
                "ORDER BY System.DateModified DESC";

            try
            {
                using var connection = new OleDbConnection(ConnectionString);
                connection.Open();
                using var command = new OleDbCommand(sql, connection);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string? path = reader.IsDBNull(0) ? null : reader.GetString(0);
                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        continue;
                    // The real file name, extension included — Windows' own display name
                    // can hide extensions depending on Explorer settings.
                    results.Add(new FileResult(Path.GetFileName(path), path));
                }
            }
            catch
            {
                // Index unavailable or the query was rejected — just no file results.
            }

            return results;
        }
    }
}
