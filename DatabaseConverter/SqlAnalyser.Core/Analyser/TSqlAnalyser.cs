﻿using SqlAnalyser.Model;
using System;
using System.Linq;
using System.Text;
using DatabaseInterpreter.Model;
using System.Collections.Generic;

namespace SqlAnalyser.Core
{
    public class TSqlAnalyser : SqlAnalyserBase
    {
        private TSqlRuleAnalyser ruleAnalyser = null;

        public override DatabaseType DatabaseType => DatabaseType.SqlServer;

        public TSqlAnalyser()
        {
            this.ruleAnalyser = new TSqlRuleAnalyser();
        }

        public override ViewScript AnalyseView(string content)
        {
            ViewScript view = this.ruleAnalyser.AnalyseView(content);

            return view;
        }

        public override RoutineScript AnalyseProcedure(string content)
        {
            RoutineScript procedure = this.ruleAnalyser.AnalyseProcedure(content);

            procedure.Type = RoutineType.PROCEDURE;

            return procedure;
        }

        public override RoutineScript AnalyseFunction(string content)
        {
            RoutineScript function = this.ruleAnalyser.AnalyseFunction(content);
            function.Type = RoutineType.FUNCTION;

            return function;
        }

        public override TriggerScript AnalyseTrigger(string content)
        {
            TriggerScript trigger = this.ruleAnalyser.AnalyseTrigger(content);

            return trigger;
        }

        public override string GenerateScripts(CommonScript script)
        {
            if (script is RoutineScript routineScript)
            {
                return this.GenerateRoutineScripts(routineScript);
            }
            else if (script is ViewScript viewScript)
            {
                return this.GenearteViewScripts(viewScript);
            }
            else if (script is TriggerScript triggerScript)
            {
                return this.GenearteTriggerScripts(triggerScript);
            }
            else
            {
                throw new NotSupportedException($"Not support generate scripts for type: {script.GetType()}.");
            }
        }

        public string GenerateRoutineScripts(RoutineScript script)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"CREATE {script.Type.ToString()} {script.FullName}");

            if (script.Parameters.Count > 0)
            {
                sb.AppendLine("(");

                int i = 0;
                foreach (Parameter parameter in script.Parameters)
                {
                    ParameterType parameterType = parameter.ParameterType;

                    string strParameterType = "";

                    if (parameterType.HasFlag(ParameterType.IN) && parameterType.HasFlag(ParameterType.OUT))
                    {
                        strParameterType = "INOUT";
                    }
                    else if (parameterType != ParameterType.NONE)
                    {
                        strParameterType = parameterType.ToString();
                    }

                    string defaultValue = parameter.DefaultValue == null ? "" : "=" + parameter.DefaultValue;

                    sb.AppendLine($"{strParameterType} {parameter.Name} {parameter.DataType} {defaultValue} {(i == script.Parameters.Count - 1 ? "" : ",")}");

                    i++;
                }

                sb.AppendLine(")");
            }
            else if (script.Type == RoutineType.FUNCTION)
            {
                sb.AppendLine("(");
                sb.AppendLine(")");
            }

            if (script.Type == RoutineType.FUNCTION)
            {
                if (script.ReturnTable == null)
                {
                    sb.AppendLine($"RETURNS {script.ReturnDataType}");
                }
                else
                {
                    sb.AppendLine($"RETURNS {script.ReturnTable.Name}({string.Join(",", script.ReturnTable.Columns.Select(t => $"{t.Name} {t.DataType}")) })");
                }
            }

            sb.AppendLine("AS");

            sb.AppendLine("BEGIN");

            foreach (Statement statement in script.Statements)
            {
                sb.Append(this.BuildStatement(statement));
            }

            sb.AppendLine("END");

            return this.FormatScripts(sb.ToString());
        }

        public string GenearteViewScripts(ViewScript script)
        {
            StringBuilder sb = new StringBuilder();

            sb.AppendLine($"CREATE VIEW {script.FullName} AS");

            foreach (SelectStatement statement in script.Statements.Where(item => item is SelectStatement))
            {
                sb.AppendLine($"SELECT {string.Join("," + Environment.NewLine, statement.Columns.Select(item => item.ToString()))}");

                sb.AppendLine($"FROM {statement.TableName}");

                if (statement.Condition != null)
                {
                    sb.AppendLine($"WHERE {statement.Condition}");
                }

                sb.Append(";");
            }

            return this.FormatScripts(sb.ToString());
        }

        public string GenearteTriggerScripts(TriggerScript script)
        {
            StringBuilder sb = new StringBuilder();

            string time = script.Time == TriggerTime.BEFORE ? "INSTEAD OF" : script.Time.ToString();
            string events = string.Join(",", script.Events);

            sb.AppendLine($"CREATE TRIGGER {script.FullName} ON {script.TableName}");
            sb.AppendLine($"{time} {events} NOT FOR REPLICATION ");

            sb.AppendLine("AS");
            sb.AppendLine("BEGIN");

            foreach (Statement statement in script.Statements)
            {
                sb.Append(this.BuildStatement(statement));
            }

            sb.AppendLine("END");

            return this.FormatScripts(sb.ToString());
        }

        private string BuildStatement(Statement statement, int level = 0, bool appendSeparator = true)
        {
            StringBuilder sb = new StringBuilder();

            string indent = " ".PadLeft((level + 1) * 2);

            Action<string> append = (value) => { sb.Append($"{indent}{value}"); };

            Action<string> appendLine = (value) => { append(value + Environment.NewLine); };

            Action<IEnumerable<Statement>, bool> appendStatements = (statements, needSeparator) =>
            {
                foreach (Statement st in statements)
                {
                    append(this.BuildStatement(st, level + 1, needSeparator));
                }
            };

            if (statement is SelectStatement select)
            {
                appendLine($"SELECT {string.Join("," + Environment.NewLine + indent, select.Columns.Select(item => item.ToString()))}");

                if (select.IntoTableName != null)
                {
                    appendLine($"INTO {select.IntoTableName.ToString()}");
                }

                if (select.TableName != null)
                {
                    if (select.WithStatements == null || select.WithStatements.Count == 0)
                    {
                        appendLine($"FROM {select.TableName}");
                    }
                    else
                    {
                        int i = 0;

                        foreach (WithStatement withStatement in select.WithStatements)
                        {
                            if (i == 0)
                            {
                                appendLine($"WITH {withStatement.Name}({string.Join(",", withStatement.Columns.Select(item => item))})");
                            }
                            else
                            {
                                appendLine($",{withStatement.Name}");
                            }

                            appendLine("AS(");

                            appendStatements(select.WithStatements, false);

                            appendLine(")");
                        }

                        appendLine(select.TableName.ToString());

                        if (select.OrderBy != null)
                        {
                            appendLine(select.OrderBy.ToString());
                        }

                        if (select.Option != null)
                        {
                            append(select.Option.ToString());
                        }
                    }
                }

                if (select.Condition != null)
                {
                    append($"WHERE {select.Condition}");
                }

                if (select.UnionStatements != null)
                {
                    foreach (var union in select.UnionStatements)
                    {
                        appendLine("UNION");
                        appendLine(this.BuildStatement(union, level, false).TrimEnd(';'));
                    }
                }

                if (appendSeparator)
                {
                    appendLine(";");
                }
            }
            else if (statement is InsertStatement insert)
            {
                append($"INSERT INTO {insert.TableName}");

                if (insert.Columns.Count > 0)
                {
                    appendLine($"({ string.Join(",", insert.Columns.Select(item => item.ToString()))})");
                }

                if (insert.SelectStatements != null && insert.SelectStatements.Count > 0)
                {
                    appendStatements(insert.SelectStatements, true);
                }
                else
                {
                    appendLine($"VALUES({string.Join(",", insert.Values.Select(item => item))});");
                }

                appendLine("");
            }
            else if (statement is UpdateStatement update)
            {
                appendLine($"UPDATE {string.Join(",", update.TableNames)} SET");

                appendLine(string.Join("," + Environment.NewLine + indent, update.SetItems.Select(item => $"{item.Name}={item.Value}")));

                if (update.FromItems != null)
                {
                    appendLine("FROM");

                    int i = 0;
                    foreach (UpdateFromItem fromItem in update.FromItems)
                    {
                        if (fromItem.TableName != null)
                        {
                            appendLine(fromItem.TableName.ToString());
                        }
                        else
                        {
                            if (i == 0)
                            {
                                appendLine(update.TableNames.First().ToString());
                            }
                        }

                        foreach (TokenInfo join in fromItem.Joins)
                        {
                            appendLine(join.ToString());
                        }

                        i++;
                    }
                }

                if (update.Condition != null && update.Condition.Symbol != null)
                {
                    appendLine($"WHERE {update.Condition}");
                }

                if (update.Option != null)
                {
                    appendLine(update.Option.ToString());
                }

                appendLine(";");
            }
            else if (statement is DeleteStatement delete)
            {
                appendLine($"DELETE FROM {delete.TableName}");

                if (delete.Condition != null)
                {
                    appendLine($"WHERE {delete.Condition}");
                }

                appendLine(";");
            }
            else if (statement is DeclareStatement declare)
            {
                if (declare.Type == DeclareType.Variable)
                {
                    appendLine($"DECLARE {declare.Name} {declare.DataType};");
                }
                else if (declare.Type == DeclareType.Table)
                {
                    appendLine($"DECLARE {declare.Name} TABLE (");

                    int i = 0;
                    foreach (var column in declare.Table.Columns)
                    {
                        appendLine($"{column.Name} {column.DataType}{(i == declare.Table.Columns.Count - 1 ? "" : ",")}");
                    }

                    appendLine(")");
                }
            }
            else if (statement is SetStatement set)
            {
                if (set.Key != null && set.Value != null)
                {
                    appendLine($"SET {set.Key } = {set.Value };");
                }
            }
            else if (statement is IfStatement @if)
            {
                foreach (IfStatementItem item in @if.Items)
                {
                    if (item.Type == IfStatementType.IF)
                    {
                        appendLine($"{item.Type} {item.Condition}");
                    }
                    else
                    {
                        appendLine($"{item.Type}");
                    }

                    appendLine("BEGIN");

                    if (item.Statements.Count > 0)
                    {
                        appendStatements(item.Statements, true);
                    }
                    else
                    {
                        appendLine("PRINT('BLANK!');");
                    }

                    appendLine("END");
                }

                appendLine("");
            }
            else if (statement is WhileStatement @while)
            {
                appendLine($"WHILE { @while.Condition }");
                appendLine("BEGIN");

                appendStatements(@while.Statements, true);

                appendLine("END");

                appendLine("");
            }
            else if (statement is TryCatchStatement tryCatch)
            {
                appendLine("BEGIN TRY");
                appendStatements(tryCatch.TryStatements, true);
                appendLine("END TRY");

                appendLine("BEGIN CATCH");
                appendStatements(tryCatch.CatchStatements, true);
                appendLine("END CATCH");

            }
            else if (statement is ReturnStatement @return)
            {
                appendLine($"RETURN {@return.Value};");
            }
            else if (statement is PrintStatement print)
            {
                appendLine($"PRINT {print.Content};");
            }
            else if (statement is ExecuteStatement execute)
            {
                appendLine($"EXECUTE {execute.Content};");
            }
            else if (statement is TransactionStatement transaction)
            {
                TransactionCommandType commandType = transaction.CommandType;

                switch (commandType)
                {
                    case TransactionCommandType.BEGIN:
                        appendLine("BEGIN TRANS");
                        break;
                    case TransactionCommandType.COMMIT:
                        appendLine("COMMIT");
                        break;
                    case TransactionCommandType.ROLLBACK:
                        appendLine("ROLLBACK");
                        break;
                }
            }
            else if (statement is LeaveStatement leave)
            {
                appendLine("RETURN;");
            }

            return sb.ToString();
        }
    }
}