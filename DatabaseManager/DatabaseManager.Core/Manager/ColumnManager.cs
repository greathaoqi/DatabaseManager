﻿using DatabaseInterpreter.Core;
using DatabaseInterpreter.Model;
using DatabaseInterpreter.Utility;
using DatabaseManager.Model;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace DatabaseManager.Core
{
    public class ColumnManager
    {
        public static IEnumerable<DataTypeDesignerInfo> GetDataTypeInfos(DatabaseType databaseType)
        {
            List<DataTypeDesignerInfo> dataTypeDesignerInfos = new List<DataTypeDesignerInfo>();

            IEnumerable<DataTypeSpecification> dataTypeSpecifications = DataTypeManager.GetDataTypeSpecifications(databaseType);

            foreach (DataTypeSpecification dataTypeSpec in dataTypeSpecifications)
            {
                DataTypeDesignerInfo dataTypeDesingerInfo = new DataTypeDesignerInfo();

                ObjectHelper.CopyProperties(dataTypeSpec, dataTypeDesingerInfo);

                dataTypeDesignerInfos.Add(dataTypeDesingerInfo);
            }

            return dataTypeDesignerInfos;
        }

        public static bool ValidateDataType(DatabaseType databaseType, TableColumnDesingerInfo columnDesingerInfo, out string message)
        {
            message = "";

            string columName = columnDesingerInfo.Name;
            string dataType = columnDesingerInfo.DataType;

            DataTypeSpecification dataTypeSpec = DataTypeManager.GetDataTypeSpecification(databaseType, dataType);

            if (dataTypeSpec == null)
            {
                message = $"Invalid data type:{dataType}";
                return false;
            }

            if (!string.IsNullOrEmpty(dataTypeSpec.Args))
            {
                string length = columnDesingerInfo.Length?.Trim();

                if (string.IsNullOrEmpty(length) && dataTypeSpec.Optional)
                {
                    return true;
                }

                if (dataTypeSpec.AllowMax && !string.IsNullOrEmpty(length) && length.ToLower() == "max")
                {
                    return true;
                }

                string args = dataTypeSpec.Args;

                string[] argsNames = args.Split(',');
                string[] lengthItems = length?.Split(',');

                if (argsNames.Length != lengthItems.Length)
                {
                    message = $"Length is invalid for column \"{columName}\", it's format should be:{args}";
                    return false;
                }

                int i = 0;

                foreach (string argName in argsNames)
                {
                    string lengthItem = lengthItems[i];

                    ArgumentRange? range = DataTypeManager.GetArgumentRange(dataTypeSpec, argName);

                    if (range.HasValue)
                    {
                        int lenValue;

                        if (!int.TryParse(lengthItem, out lenValue))
                        {
                            message = $"\"{lengthItem}\" is't a valid integer value";
                            return false;
                        }

                        if (lenValue < range.Value.Min || lenValue > range.Value.Max)
                        {
                            message = $"The \"{argName}\"'s range of column \"{columName}\" should be between {range.Value.Min} and {range.Value.Max}";
                            return false;
                        }
                    }

                    i++;
                }
            }

            return true;
        }

        public static void SetColumnLength(DatabaseType databaseType, TableColumn column, string length)
        {
            string dataType = column.DataType;
            DataTypeSpecification dataTypeSpec = DataTypeManager.GetDataTypeSpecification(databaseType, dataType);

            string args = dataTypeSpec.Args;

            if (string.IsNullOrEmpty(args))
            {
                return;
            }

            string[] argsNames = args.Split(',');
            string[] lengthItems = length.Split(',');

            int i = 0;

            foreach (string argName in argsNames)
            {
                string lengthItem = lengthItems[i];

                if (argName == "length")
                {
                    bool isChar = DataTypeHelper.IsCharType(dataType);

                    if (isChar)
                    {
                        if (dataTypeSpec.AllowMax && lengthItem.ToLower() == "max")
                        {
                            column.MaxLength = -1;
                        }
                        else
                        {
                            column.MaxLength = long.Parse(lengthItem) * (DataTypeHelper.StartWithN(dataType) ? 2 : 1);
                        }
                    }
                    else
                    {
                        column.MaxLength = long.Parse(lengthItem);
                    }
                }
                else if (argName == "precision")
                {
                    column.Precision = int.Parse(lengthItem);
                }
                else if (argName == "scale")
                {
                    column.Scale = int.Parse(lengthItem);
                }

                i++;
            }
        }
    }
}