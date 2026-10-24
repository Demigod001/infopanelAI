using System.Data;
using InfoPanel.Plugins;
using InfoPanel.Plugins.Ipc;

namespace InfoPanel.Plugins.Host
{
    internal static class TableDtoConverter
    {
        public static TableValueDto ConvertTableToDto(IPluginTable table)
        {
            return TryConvert(table, out var dto, out _)
                ? dto
                : new TableValueDto { DefaultFormat = table.DefaultFormat };
        }

        /// <summary>
        /// Reads the table once into a DTO and a hash. Returns false if another thread
        /// mutates the DataTable during the read. Callers must skip that update rather
        /// than treat the failure as "nothing changed".
        /// </summary>
        public static bool TryConvert(IPluginTable table, out TableValueDto dto, out int hash)
        {
            dto = new TableValueDto
            {
                DefaultFormat = table.DefaultFormat,
                Columns = [],
                Rows = []
            };
            hash = 0;

            var dt = table.Value;
            if (dt == null) return true;

            try
            {
                var colCount = dt.Columns.Count;
                for (int c = 0; c < colCount; c++)
                    dto.Columns.Add(dt.Columns[c].ColumnName);

                var rowCount = dt.Rows.Count;
                var rowRefs = new DataRow[rowCount];
                dt.Rows.CopyTo(rowRefs, 0);

                hash = HashCode.Combine(rowCount, colCount);

                foreach (var row in rowRefs)
                {
                    if (row.RowState is DataRowState.Detached or DataRowState.Deleted)
                        return false;

                    var rowCells = new List<TableCellDto>(colCount);
                    for (int i = 0; i < colCount; i++)
                    {
                        var cell = new TableCellDto();
                        var value = row[i, DataRowVersion.Default];
                        if (value is IPluginSensor cellSensor)
                        {
                            cell.Type = "sensor";
                            cell.SensorName = cellSensor.Name;
                            cell.SensorUnit = cellSensor.Unit;
                            cell.SensorValue = new SensorValueDto
                            {
                                Value = cellSensor.Value,
                                ValueMin = cellSensor.ValueMin,
                                ValueMax = cellSensor.ValueMax,
                                ValueAvg = cellSensor.ValueAvg,
                                Unit = cellSensor.Unit
                            };
                            hash = HashCode.Combine(hash, cellSensor.Value, cellSensor.ValueMin, cellSensor.ValueMax, cellSensor.ValueAvg);
                        }
                        else if (value is IPluginText cellText)
                        {
                            cell.Type = "text";
                            cell.TextValue = cellText.Value;
                            hash = HashCode.Combine(hash, cellText.Value?.GetHashCode() ?? 0);
                        }
                        else
                        {
                            cell.Type = "text";
                            cell.TextValue = value?.ToString() ?? "";
                            hash = HashCode.Combine(hash, value?.GetHashCode() ?? 0);
                        }
                        rowCells.Add(cell);
                    }
                    dto.Rows.Add(rowCells);
                }

                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or RowNotInTableException
                or NullReferenceException
                or IndexOutOfRangeException
                or ArgumentException)
            {
                // DataTable is not thread-safe. A plugin that clears rows while this
                // runs throws one of these from the internal row index.
                return false;
            }
        }
    }
}
