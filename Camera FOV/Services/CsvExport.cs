using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Camera_FOV.Services
{
    /// <summary>
    /// A table written as CSV that Excel opens directly: UTF-8 with a byte order mark, the list
    /// separator and decimal mark of this PC's regional settings, and a few title lines above the
    /// column headers.
    /// </summary>
    public sealed class CsvTable
    {
        private readonly List<string> _titles = new List<string>();
        private readonly List<string> _columns = new List<string>();
        private readonly List<List<string>> _rows = new List<List<string>>();

        public CsvTable Title(string line) { _titles.Add(line); return this; }

        public CsvTable Columns(params string[] names) { _columns.AddRange(names); return this; }

        public void Row(params object[] cells) => _rows.Add(cells.Select(Format).ToList());

        public int RowCount => _rows.Count;

        private static string Format(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case double d when double.IsNaN(d) || double.IsInfinity(d): return string.Empty;
                case double d: return d.ToString("0.##", CultureInfo.CurrentCulture);
                case float f: return ((double)f).ToString("0.##", CultureInfo.CurrentCulture);
                case bool b: return b ? "Yes" : "No";
                default: return Convert.ToString(value, CultureInfo.CurrentCulture);
            }
        }

        public void Save(string path)
        {
            string separator = CultureInfo.CurrentCulture.TextInfo.ListSeparator;
            if (string.IsNullOrEmpty(separator)) separator = ",";

            string Quote(string cell)
            {
                cell = cell ?? string.Empty;
                bool needs = cell.Contains(separator) || cell.Contains("\"") || cell.Contains("\n") || cell.Contains("\r");
                return needs ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
            }

            var text = new StringBuilder();
            foreach (string title in _titles) text.AppendLine(Quote(title));
            if (_titles.Any()) text.AppendLine();
            text.AppendLine(string.Join(separator, _columns.Select(Quote)));
            foreach (List<string> row in _rows) text.AppendLine(string.Join(separator, row.Select(Quote)));

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        }
    }
}
