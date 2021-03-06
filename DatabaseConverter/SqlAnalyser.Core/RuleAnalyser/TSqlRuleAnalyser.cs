﻿using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using DatabaseInterpreter.Utility;
using SqlAnalyser.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static TSqlParser;

namespace SqlAnalyser.Core
{
    public class TSqlRuleAnalyser : SqlRuleAnalyser
    {
        public override Lexer GetLexer(string content)
        {
            return new TSqlLexer(this.GetCharStreamFromString(content));
        }

        public override Parser GetParser(CommonTokenStream tokenStream)
        {
            return new TSqlParser(tokenStream);
        }

        public Tsql_fileContext GetRootContext(string content, out SqlSyntaxError error)
        {
            error = null;

            TSqlParser parser = this.GetParser(content) as TSqlParser;

            SqlSyntaxErrorListener errorListener = this.AddParserErrorListener(parser);

            Tsql_fileContext context = parser.tsql_file();

            error = errorListener.Error;

            return context;
        }

        public Ddl_clauseContext GetDdlStatementContext(string content, out SqlSyntaxError error)
        {
            error = null;

            Tsql_fileContext rootContext = this.GetRootContext(content, out error);

            return rootContext?.batch().FirstOrDefault()?.sql_clauses()?.sql_clause().Select(item => item?.ddl_clause()).FirstOrDefault();
        }

        public override AnalyseResult AnalyseProcedure(string content)
        {
            SqlSyntaxError error = null;

            Ddl_clauseContext ddlStatement = this.GetDdlStatementContext(content, out error);

            AnalyseResult result = new AnalyseResult() { Error = error };

            if (!result.HasError && ddlStatement != null)
            {
                RoutineScript script = new RoutineScript() { Type = RoutineType.PROCEDURE };

                Create_or_alter_procedureContext proc = ddlStatement.create_or_alter_procedure();

                if (proc != null)
                {
                    #region Name
                    this.SetScriptName(script, proc.func_proc_name_schema().id());
                    #endregion

                    #region Parameters
                    this.SetRoutineParameters(script, proc.procedure_param());
                    #endregion

                    #region Body

                    this.SetScriptBody(script, proc.sql_clauses().sql_clause());

                    #endregion
                }

                this.ExtractFunctions(script, ddlStatement);

                result.Script = script;
            }


            return result;
        }

        public void SetScriptBody(CommonScript script, Sql_clauseContext[] clauses)
        {
            foreach (var clause in clauses)
            {
                script.Statements.AddRange(this.ParseSqlClause(clause));
            }
        }

        public override AnalyseResult AnalyseFunction(string content)
        {
            SqlSyntaxError error = null;

            Ddl_clauseContext ddlStatement = this.GetDdlStatementContext(content, out error);

            AnalyseResult result = new AnalyseResult() { Error = error };

            if (!result.HasError && ddlStatement != null)
            {
                RoutineScript script = new RoutineScript() { Type = RoutineType.FUNCTION };

                Create_or_alter_functionContext func = ddlStatement.create_or_alter_function();

                if (func != null)
                {
                    #region Name
                    this.SetScriptName(script, func.func_proc_name_schema().id());
                    #endregion

                    #region Parameters
                    this.SetRoutineParameters(script, func.procedure_param());
                    #endregion

                    this.SetFunction(script, func);
                }

                this.ExtractFunctions(script, ddlStatement);

                result.Script = script;
            }

            return result;
        }

        public void SetFunction(RoutineScript script, Create_or_alter_functionContext func)
        {
            var scalar = func.func_body_returns_scalar();
            var table = func.func_body_returns_table();
            var select = func.func_body_returns_select();

            if (scalar != null)
            {
                script.ReturnDataType = new TokenInfo(scalar.data_type().GetText()) { Type = TokenType.DataType };

                this.SetScriptBody(script, scalar.sql_clause());

                #region ReturnStatement
                IParseTree t = null;
                for (var i = scalar.children.Count - 1; i >= 0; i--)
                {
                    if (scalar.children[i] is TerminalNodeImpl terminalNode)
                    {
                        if (terminalNode.Symbol.Type == TSqlParser.RETURN)
                        {
                            if (t != null)
                            {
                                ReturnStatement returnStatement = new ReturnStatement();
                                returnStatement.Value = new TokenInfo(t as ParserRuleContext);

                                script.Statements.Add(returnStatement);

                                break;
                            }
                        }
                    }

                    t = scalar.children[i];
                }
                #endregion
            }
            else if (table != null)
            {
                script.ReturnTable = new TemporaryTable();

                foreach (var child in table.children)
                {
                    if (child is TerminalNodeImpl terminalNode)
                    {
                        if (terminalNode.Symbol.Text.StartsWith("@"))
                        {
                            script.ReturnTable.Name = new TokenInfo(terminalNode) { Type = TokenType.VariableName };
                        }
                    }
                    else if (child is Table_type_definitionContext type)
                    {
                        script.ReturnTable.Columns = type.column_def_table_constraints().column_def_table_constraint()
                            .Select(item => this.ParseColumnName(item)).ToList();
                    }
                }

                script.Statements.AddRange(table.sql_clause().SelectMany(item => this.ParseSqlClause(item)));
            }
            else if (select != null)
            {
                script.Statements.AddRange(this.ParseSelectStatement(select.select_statement()));
            }
        }

        public override AnalyseResult AnalyseView(string content)
        {
            SqlSyntaxError error = null;

            Ddl_clauseContext ddlStatement = this.GetDdlStatementContext(content, out error);

            AnalyseResult result = new AnalyseResult() { Error = error };

            if (!result.HasError && ddlStatement != null)
            {
                ViewScript script = new ViewScript();

                Create_viewContext view = ddlStatement.create_view();

                if (view != null)
                {
                    #region Name
                    this.SetScriptName(script, view.simple_name().id());
                    #endregion

                    #region Statement

                    foreach (var child in view.children)
                    {
                        if (child is Select_statementContext select)
                        {
                            script.Statements.AddRange(this.ParseSelectStatement(select));
                        }
                    }

                    #endregion
                }

                this.ExtractFunctions(script, ddlStatement);

                result.Script = script;
            }

            return result;
        }

        public override AnalyseResult AnalyseTrigger(string content)
        {
            SqlSyntaxError error = null;

            Ddl_clauseContext ddlStatement = this.GetDdlStatementContext(content, out error);

            AnalyseResult result = new AnalyseResult() { Error = error };
            TriggerScript script = new TriggerScript();

            if (!result.HasError && ddlStatement != null)
            {
                Create_or_alter_dml_triggerContext trigger = ddlStatement.create_or_alter_trigger().create_or_alter_dml_trigger();

                if (trigger != null)
                {
                    #region Name                 

                    this.SetScriptName(script, trigger.simple_name().id());

                    #endregion

                    script.TableName = new TokenInfo(trigger.table_name()) { Type = TokenType.TableName };

                    foreach (var child in trigger.children)
                    {
                        if (child is TerminalNodeImpl terminalNode)
                        {
                            switch (terminalNode.Symbol.Type)
                            {
                                case TSqlParser.BEFORE:
                                    script.Time = TriggerTime.BEFORE;
                                    break;
                                case TSqlParser.INSTEAD:
                                    script.Time = TriggerTime.INSTEAD_OF;
                                    break;
                                case TSqlParser.AFTER:
                                    script.Time = TriggerTime.AFTER;
                                    break;
                            }
                        }
                        else if (child is Dml_trigger_operationContext operation)
                        {
                            script.Events.Add((TriggerEvent)Enum.Parse(typeof(TriggerEvent), operation.GetText()));
                        }
                    }

                    #region Body

                    this.SetScriptBody(script, trigger.sql_clauses().sql_clause());

                    #endregion
                }

                this.ExtractFunctions(script, ddlStatement);

                result.Script = script;
            }

            return result;
        }

        public List<Statement> ParseSqlClause(Sql_clauseContext node)
        {
            List<Statement> statements = new List<Statement>();

            foreach (var bc in node.children)
            {
                if (bc is Another_statementContext another)
                {
                    statements.AddRange(this.ParseAnotherStatement(another));
                }
                else if (bc is Dml_clauseContext dml)
                {
                    statements.AddRange(this.ParseDmlStatement(dml));
                }
                else if (bc is Ddl_clauseContext ddl)
                {
                    statements.AddRange(this.ParseDdlStatement(ddl));
                }
                else if (bc is Cfl_statementContext cfl)
                {
                    statements.AddRange(this.ParseCflStatement(cfl));
                }
            }

            return statements;
        }

        public List<Statement> ParseDmlStatement(Dml_clauseContext node)
        {
            List<Statement> statements = new List<Statement>();

            if (node.children != null)
            {
                foreach (var child in node.children)
                {
                    if (child is Select_statementContext select)
                    {
                        statements.AddRange(this.ParseSelectStatement(select));
                    }
                    if (child is Insert_statementContext insert)
                    {
                        statements.Add(this.ParseInsertStatement(insert));
                    }
                    else if (child is Update_statementContext update)
                    {
                        statements.Add(this.ParseUpdateStatement(update));
                    }
                    else if (child is Delete_statementContext delete)
                    {
                        statements.Add(this.ParseDeleteStatement(delete));
                    }
                }
            }

            return statements;
        }

        public List<Statement> ParseDdlStatement(Ddl_clauseContext node)
        {
            List<Statement> statements = new List<Statement>();

            if (node.children != null)
            {
                foreach (var child in node.children)
                {
                    if (child is Truncate_tableContext truncate)
                    {
                        TruncateStatement truncateStatement = new TruncateStatement();

                        truncateStatement.TableName = this.ParseTableName(truncate.table_name());

                        statements.Add(truncateStatement);
                    }
                }
            }

            return statements;
        }

        public InsertStatement ParseInsertStatement(Insert_statementContext node)
        {
            InsertStatement statement = new InsertStatement();

            foreach (var child in node.children)
            {
                if (child is Ddl_objectContext table)
                {
                    statement.TableName = this.ParseTableName(table);
                }
                else if (child is Column_name_listContext columns)
                {
                    statement.Columns = columns.id().Select(item => this.ParseColumnName(item)).ToList();
                }
                else if (child is Insert_statement_valueContext values)
                {
                    var tableValues = values.table_value_constructor();
                    var derivedTable = values.derived_table();

                    if (tableValues != null)
                    {
                        statement.Values = tableValues.expression_list().SelectMany(item => item.expression().Select(t => new TokenInfo(t))).ToList();
                    }
                    else if (derivedTable != null)
                    {
                        statement.SelectStatements = new List<SelectStatement>();

                        statement.SelectStatements.AddRange(this.ParseSelectStatement(derivedTable.subquery().select_statement()));
                    }
                }
            }

            return statement;
        }

        public UpdateStatement ParseUpdateStatement(Update_statementContext node)
        {
            UpdateStatement statement = new UpdateStatement();

            Ddl_objectContext name = node.ddl_object();

            statement.TableNames.Add(this.ParseTableName(name));

            foreach (var ele in node.update_elem())
            {
                statement.SetItems.Add(new NameValueItem()
                {
                    Name = new TokenInfo(ele.full_column_name()) { Type = TokenType.ColumnName },
                    Value = this.ParseToken(ele.expression())
                });
            }

            Table_sourcesContext fromTable = node.table_sources();

            if (fromTable != null)
            {
                statement.FromItems = this.ParseTableScources(fromTable);
            }

            statement.Condition = this.ParseCondition(node.search_condition_list());

            return statement;
        }

        public List<FromItem> ParseTableScources(Table_sourcesContext node)
        {
            List<FromItem> fromItems = new List<FromItem>();

            foreach (var child in node.children)
            {
                if (child is Table_sourceContext ts)
                {
                    FromItem fromItem = new FromItem();

                    Table_source_item_joinedContext tsi = ts.table_source_item_joined();

                    Table_source_itemContext fromTable = tsi.table_source_item();
                    As_table_aliasContext alias = fromTable.as_table_alias();

                    if (alias != null)
                    {
                        fromItem.Alias = new TokenInfo(alias.table_alias()) { Type = TokenType.Alias };
                    }

                    Derived_tableContext derivedTable = fromTable.derived_table();

                    if (derivedTable != null)
                    {
                        fromItem.SubSelectStatement = this.ParseDerivedTable(derivedTable);
                    }
                    else
                    {
                        fromItem.TableName = this.ParseTableName(fromTable);
                    }

                    Join_partContext[] joins = tsi.join_part();

                    if (joins != null && joins.Length > 0)
                    {
                        foreach (Join_partContext join in joins)
                        {
                            List<JoinItem> joinItems = this.ParseJoin(join);

                            if (joinItems.Count > 1)
                            {
                                for (int i = joinItems.Count - 1; i > 0; i--)
                                {
                                    JoinItem currentJoinItem = joinItems[i];

                                    if (i - 1 > 0)
                                    {
                                        JoinItem previousJoinItem = joinItems[i - 1];

                                        TableName previousJoinTableName = new TableName(previousJoinItem.TableName.Symbol);
                                        ObjectHelper.CopyProperties(previousJoinItem.TableName, previousJoinTableName);

                                        TableName currentJoinTableName = new TableName(currentJoinItem.TableName.Symbol);
                                        ObjectHelper.CopyProperties(currentJoinItem.TableName, currentJoinTableName);

                                        joinItems[i - 1].TableName = currentJoinTableName;
                                        joinItems[i].TableName = previousJoinTableName;
                                    }
                                }
                            }

                            fromItem.JoinItems.AddRange(joinItems);
                        }
                    }

                    fromItems.Add(fromItem);
                }
            }

            return fromItems;
        }

        public List<JoinItem> ParseJoin(Join_partContext node)
        {
            List<JoinItem> joinItems = new List<JoinItem>();

            JoinItem joinItem = new JoinItem();

            foreach (var child in node.children)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    switch (type)
                    {
                        case TSqlParser.INNER:
                            joinItem.Type = JoinType.INNER;
                            break;
                        case TSqlParser.LEFT:
                            joinItem.Type = JoinType.LEFT;
                            break;
                        case TSqlParser.RIGHT:
                            joinItem.Type = JoinType.RIGHT;
                            break;
                        case TSqlParser.FULL:
                            joinItem.Type = JoinType.FULL;
                            break;
                        case TSqlParser.CROSS:
                            joinItem.Type = JoinType.CROSS;
                            break;
                        case TSqlParser.PIVOT:
                            joinItem.Type = JoinType.PIVOT;
                            break;
                        case TSqlParser.UNPIVOT:
                            joinItem.Type = JoinType.UNPIVOT;
                            break;
                    }
                }
            }

            Table_sourceContext tableSoure = node.table_source();
            Pivot_clauseContext pivot = node.pivot_clause();
            Unpivot_clauseContext unpivot = node.unpivot_clause();

            As_table_aliasContext alias = node.as_table_alias();

            if (alias != null)
            {
                joinItem.Alias = new TokenInfo(alias.table_alias());
            }

            joinItems.Add(joinItem);

            if (tableSoure != null)
            {
                joinItem.TableName = this.ParseTableName(tableSoure);
                joinItem.Condition = this.ParseCondition(node.search_condition());

                Table_source_item_joinedContext join = tableSoure.table_source_item_joined();

                if (join != null)
                {
                    Join_partContext[] joinParts = join.join_part();

                    List<JoinItem> childJoinItems = joinParts.SelectMany(item => this.ParseJoin(item)).ToList();

                    joinItems.AddRange(childJoinItems);
                }
            }
            else if (pivot != null)
            {
                joinItem.PivotItem = this.ParsePivot(pivot);
            }
            else if (unpivot != null)
            {
                joinItem.UnPivotItem = this.ParseUnPivot(unpivot);
            }

            return joinItems;
        }

        public PivotItem ParsePivot(Pivot_clauseContext node)
        {
            PivotItem pivotItem = new PivotItem();

            Aggregate_windowed_functionContext function = node.aggregate_windowed_function();

            pivotItem.AggregationFunctionName = new TokenInfo(function.children[0] as TerminalNodeImpl);
            pivotItem.AggregatedColumnName = this.ParseColumnName(function.all_distinct_expression()?.expression());
            pivotItem.ColumnName = this.ParseColumnName(node.full_column_name());
            pivotItem.Values = node.column_alias_list().column_alias().Select(item => new TokenInfo(item)).ToList();

            return pivotItem;
        }

        public UnPivotItem ParseUnPivot(Unpivot_clauseContext node)
        {
            UnPivotItem unpivotItem = new UnPivotItem();
            unpivotItem.ValueColumnName = this.ParseColumnName(node.expression().full_column_name());
            unpivotItem.ForColumnName = this.ParseColumnName(node.full_column_name());
            unpivotItem.InColumnNames = node.full_column_name_list().full_column_name().Select(item => this.ParseColumnName(item)).ToList();

            return unpivotItem;
        }

        public SelectStatement ParseDerivedTable(Derived_tableContext node)
        {
            SelectStatement statement = new SelectStatement();

            foreach (var child in node.children)
            {
                if (child is SubqueryContext subquery)
                {
                    statement = this.ParseSelectStatement(subquery.select_statement()).FirstOrDefault();
                }
            }

            return statement;
        }

        public DeleteStatement ParseDeleteStatement(Delete_statementContext node)
        {
            DeleteStatement statement = new DeleteStatement();

            statement.TableName = this.ParseTableName(node.delete_statement_from().ddl_object());
            statement.Condition = this.ParseCondition(node.search_condition());

            return statement;
        }

        public List<Statement> ParseCflStatement(Cfl_statementContext node)
        {
            List<Statement> statements = new List<Statement>();

            foreach (var child in node.children)
            {
                if (child is If_statementContext @if)
                {
                    statements.Add(this.ParseIfStatement(@if));
                }
                if (child is While_statementContext @while)
                {
                    statements.Add(this.ParseWhileStatement(@while));
                }
                else if (child is Block_statementContext block)
                {
                    foreach (var bc in block.children)
                    {
                        if (bc is Sql_clausesContext clauses)
                        {
                            statements.AddRange(clauses.sql_clause().SelectMany(item => this.ParseSqlClause(item)));
                        }
                    }
                }
                else if (child is Return_statementContext @return)
                {
                    statements.Add(this.ParseReturnStatement(@return));
                }
                else if (child is Try_catch_statementContext trycatch)
                {
                    statements.Add(this.ParseTryCatchStatement(trycatch));
                }
                else if (child is Print_statementContext print)
                {
                    statements.Add(this.ParsePrintStatement(print));
                }
            }

            return statements;
        }

        public IfStatement ParseIfStatement(If_statementContext node)
        {
            IfStatement statement = new IfStatement();

            IfStatementItem ifItem = new IfStatementItem() { Type = IfStatementType.IF };
            IfStatementItem elseItem = null;

            bool isElse = false;

            foreach (var child in node.children)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    if (type == TSqlParser.ELSE)
                    {
                        isElse = true;
                    }
                }
                if (child is Search_conditionContext condition)
                {
                    ifItem.Condition = this.ParseCondition(condition);
                }
                else if (child is Sql_clauseContext clause)
                {
                    List<Statement> statements = this.ParseSqlClause(clause);

                    if (!isElse)
                    {
                        ifItem.Statements.AddRange(statements);
                    }
                    else
                    {
                        if (elseItem == null)
                        {
                            elseItem = new IfStatementItem() { Type = IfStatementType.ELSE };

                            elseItem.Statements.AddRange(statements);
                        }
                    }
                }
            }

            statement.Items.Add(ifItem);

            if (elseItem != null)
            {
                statement.Items.Add(elseItem);
            }

            return statement;
        }

        public WhileStatement ParseWhileStatement(While_statementContext node)
        {
            WhileStatement statement = new WhileStatement();

            foreach (var child in node.children)
            {
                if (child is Search_conditionContext condition)
                {
                    statement.Condition = this.ParseCondition(condition);
                }
                else if (child is Sql_clauseContext clause)
                {
                    statement.Statements.AddRange(this.ParseSqlClause(clause));
                }
            }

            return statement;
        }

        public List<Statement> ParseAnotherStatement(Another_statementContext node)
        {
            List<Statement> statements = new List<Statement>();

            foreach (var child in node.children)
            {
                if (child is Declare_statementContext declare)
                {
                    statements.AddRange(this.ParseDeclareStatement(declare));
                }
                else if (child is Set_statementContext set)
                {
                    statements.Add(this.ParseSetStatement(set));
                }
                else if (child is Execute_statementContext execute)
                {
                    statements.Add(this.ParseExecuteStatement(execute));
                }
                else if (child is Transaction_statementContext transaction)
                {
                    statements.Add(this.ParseTransactionStatment(transaction));
                }
                else if (child is Cursor_statementContext cursor)
                {
                    statements.Add(this.ParseCursorStatement(cursor));
                }
            }

            return statements;
        }

        public List<Statement> ParseDeclareStatement(Declare_statementContext node)
        {
            List<Statement> statements = new List<Statement>();

            foreach (var dc in node.children)
            {
                if (dc is Declare_localContext local)
                {
                    DeclareStatement declareStatement = new DeclareStatement();

                    declareStatement.Name = new TokenInfo(local.LOCAL_ID()) { Type = TokenType.VariableName };
                    declareStatement.DataType = new TokenInfo(local.data_type().GetText()) { Type = TokenType.DataType };

                    var expression = local.expression();

                    if (expression != null)
                    {
                        declareStatement.DefaultValue = new TokenInfo(expression);
                    }

                    statements.Add(declareStatement);
                }
                else if (dc is Declare_cursorContext cursor)
                {
                    statements.Add(this.ParseDeclareCursor(cursor));
                }
                else if (dc is Table_type_definitionContext table)
                {
                    DeclareStatement declareStatement = new DeclareStatement();

                    declareStatement.Type = DeclareType.Table;
                    declareStatement.Name = new TokenInfo(node.LOCAL_ID()) { Type = TokenType.VariableName };
                    declareStatement.Table = new TemporaryTable()
                    {
                        Name = declareStatement.Name,
                        Columns = table.column_def_table_constraints().column_def_table_constraint()
                        .Select(item => this.ParseColumnName(item)).ToList()
                    };

                    statements.Add(declareStatement);
                }
            }

            return statements;
        }

        public void SetScriptName(CommonScript script, IdContext[] ids)
        {
            var name = ids.Last();

            script.Name = new TokenInfo(name);

            if (ids.Length > 1)
            {
                script.Owner = new TokenInfo(ids.First());
            }
        }

        public void SetParameterType(Parameter parameterInfo, IList<IParseTree> nodes)
        {
            foreach (var child in nodes)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    if (terminalNode.Symbol.Type == TSqlParser.OUT || terminalNode.Symbol.Type == TSqlParser.OUTPUT)
                    {
                        parameterInfo.ParameterType = ParameterType.OUT;
                    }
                }
            }
        }

        public void SetRoutineParameters(RoutineScript script, Procedure_paramContext[] parameters)
        {
            if (parameters != null)
            {
                foreach (Procedure_paramContext parameter in parameters)
                {
                    Parameter parameterInfo = new Parameter();

                    parameterInfo.Name = new TokenInfo(parameter.children[0] as TerminalNodeImpl) { Type = TokenType.ParameterName };

                    parameterInfo.DataType = new TokenInfo(parameter.data_type().GetText()) { Type = TokenType.DataType };

                    var defaultValue = parameter.default_value();

                    if (defaultValue != null)
                    {
                        parameterInfo.DefaultValue = new TokenInfo(defaultValue);
                    }

                    this.SetParameterType(parameterInfo, parameter.children);

                    script.Parameters.Add(parameterInfo);
                }
            }
        }

        public List<SelectStatement> ParseSelectStatement(Select_statementContext node)
        {
            List<SelectStatement> statements = new List<SelectStatement>();

            SelectStatement selectStatement = null;

            List<WithStatement> withStatements = null;
            List<TokenInfo> orderbyList = new List<TokenInfo>();
            TokenInfo option = null;
            SelectLimitInfo selectLimitInfo = null;

            foreach (var child in node.children)
            {
                if (child is Query_expressionContext query)
                {
                    foreach (var qc in query.children)
                    {
                        if (qc is Query_specificationContext specification)
                        {
                            selectStatement = this.ParseQuerySpecification(specification);
                        }
                        else if (qc is Sql_unionContext union)
                        {
                            if (selectStatement.UnionStatements == null)
                            {
                                selectStatement.UnionStatements = new List<UnionStatement>();
                            }

                            selectStatement.UnionStatements.Add(this.ParseUnionSatement(union));
                        }
                        else if (qc is Query_expressionContext exp)
                        {
                            Query_specificationContext querySpec = exp.query_specification();

                            if (querySpec != null)
                            {
                                selectStatement = this.ParseQuerySpecification(querySpec);
                            }
                        }
                    }

                    if (selectStatement != null)
                    {
                        statements.Add(selectStatement);
                    }
                }
                else if (child is With_expressionContext with)
                {
                    withStatements = this.ParseWithStatement(with);
                }
                else if (child is Order_by_clauseContext order)
                {
                    bool isLimit = false;
                    int limitKeyword = 0;

                    foreach (var oc in order.children)
                    {
                        if (oc is Order_by_expressionContext orderByExp)
                        {
                            orderbyList.Add(this.ParseToken(orderByExp, TokenType.OrderBy));
                        }
                        else if (oc is TerminalNodeImpl terminalNode)
                        {
                            if ((limitKeyword = terminalNode.Symbol.Type) == TSqlParser.OFFSET)
                            {
                                isLimit = true;
                            }
                        }
                        else if (oc is ExpressionContext exp)
                        {
                            if (isLimit)
                            {
                                if (selectLimitInfo == null)
                                {
                                    selectLimitInfo = new SelectLimitInfo();
                                }

                                if (limitKeyword == TSqlParser.OFFSET)
                                {
                                    selectLimitInfo.StartRowIndex = new TokenInfo(exp);
                                }
                                else if (limitKeyword == TSqlParser.NEXT)
                                {
                                    selectLimitInfo.RowCount = new TokenInfo(exp);
                                }
                            }
                        }
                    }
                }
                else if (child is Option_clauseContext opt)
                {
                    option = new TokenInfo(opt) { Type = TokenType.Option };
                }

                if (selectStatement != null)
                {
                    selectStatement.WithStatements = withStatements;

                    if (orderbyList.Count > 0)
                    {
                        selectStatement.OrderBy = orderbyList;
                    }

                    if (selectLimitInfo != null)
                    {
                        selectStatement.LimitInfo = selectLimitInfo;
                    }

                    selectStatement.Option = option;
                }
            }

            return statements;
        }

        public List<WithStatement> ParseWithStatement(With_expressionContext node)
        {
            List<WithStatement> statements = new List<WithStatement>();

            var tables = node.common_table_expression();

            if (tables != null)
            {
                foreach (Common_table_expressionContext table in tables)
                {
                    WithStatement statement = new WithStatement();

                    statement.Name = new TokenInfo(table.id()) { Type = TokenType.General };
                    Column_name_listContext cols = table.column_name_list();

                    if (cols != null)
                    {
                        statement.Columns = cols.id().Select(item => this.ParseColumnName(item)).ToList();
                    }

                    statement.SelectStatements = this.ParseSelectStatement(table.select_statement());

                    statements.Add(statement);
                }
            }

            return statements;
        }

        public SelectStatement ParseQuerySpecification(Query_specificationContext node)
        {
            SelectStatement statement = new SelectStatement();

            int terminalNodeType = 0;
            foreach (var child in node.children)
            {
                if (child is Select_listContext list)
                {
                    statement.Columns.AddRange(list.select_list_elem().Select(item => this.ParseColumnName(item)));
                }
                else if (child is TerminalNodeImpl terminalNode)
                {
                    terminalNodeType = terminalNode.Symbol.Type;
                    if (terminalNodeType == TSqlParser.INTO)
                    {
                        statement.IntoTableName = new TokenInfo(node.table_name()) { Type = TokenType.TableName };
                    }
                }
                else if (child is Table_sourcesContext table)
                {
                    statement.TableName = this.ParseTableName(table);
                    statement.FromItems = this.ParseTableScources(table);
                }
                else if (child is Search_conditionContext condition)
                {
                    switch (terminalNodeType)
                    {
                        case TSqlParser.WHERE:
                            statement.Where = this.ParseCondition(condition);
                            break;
                        case TSqlParser.HAVING:
                            statement.Having = this.ParseCondition(condition);
                            break;
                    }
                }
                else if (child is Group_by_itemContext groupBy)
                {
                    if (statement.GroupBy == null)
                    {
                        statement.GroupBy = new List<TokenInfo>();
                    }

                    statement.GroupBy.Add(this.ParseToken(groupBy, TokenType.GroupBy));
                }
                else if (child is Top_clauseContext top)
                {
                    statement.TopInfo = new SelectTopInfo();
                    statement.TopInfo.TopCount = new TokenInfo(top.top_count());
                    statement.TopInfo.IsPercent = node.select_list().select_list_elem().Any(item => item.children.Any(t => t?.GetText()?.ToUpper() == "PERCENT"));
                }
            }

            return statement;
        }

        public UnionStatement ParseUnionSatement(Sql_unionContext node)
        {
            UnionStatement statement = new UnionStatement();

            UnionType unionType = UnionType.UNION;

            foreach (var child in node.children)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    switch (type)
                    {
                        case TSqlParser.ALL:
                            unionType = UnionType.UNION_ALL;
                            break;
                        case TSqlParser.INTERSECT:
                            unionType = UnionType.INTERSECT;
                            break;
                        case TSqlParser.EXCEPT:
                            unionType = UnionType.EXCEPT;
                            break;
                    }
                }
                else if (child is Query_specificationContext spec)
                {
                    statement.Type = unionType;
                    statement.SelectStatement = this.ParseQuerySpecification(spec);
                }
            }

            return statement;
        }

        public SetStatement ParseSetStatement(Set_statementContext node)
        {
            SetStatement statement = new SetStatement();

            foreach (var child in node.children)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    if (type != TSqlParser.SET && terminalNode.GetText() != "=" && statement.Key == null)
                    {
                        statement.Key = new TokenInfo(terminalNode);
                    }
                }
                else if (child is ExpressionContext exp)
                {
                    statement.Value = this.ParseToken(exp);

                    break;
                }
            }

            return statement;
        }

        public string ParseExpression(ExpressionContext node)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var child in node.children)
            {
                if (child is Full_column_nameContext columnName)
                {
                    string text = columnName.id().GetText();

                    sb.Append(text);
                }
            }

            return sb.ToString();
        }

        public Statement ParseReturnStatement(Return_statementContext node)
        {
            Statement statement = null;

            var expressioin = node.expression();

            if (expressioin != null)
            {
                statement = new ReturnStatement() { Value = new TokenInfo(expressioin) };
            }
            else
            {
                statement = new LeaveStatement() { Content = new TokenInfo(node) };
            }

            return statement;
        }

        public TryCatchStatement ParseTryCatchStatement(Try_catch_statementContext node)
        {
            TryCatchStatement statement = new TryCatchStatement();

            statement.TryStatements.AddRange(node.try_clauses.sql_clause().SelectMany(item => this.ParseSqlClause(item)));
            statement.CatchStatements.AddRange(node.catch_clauses.sql_clause().SelectMany(item => this.ParseSqlClause(item)));

            return statement;
        }

        public PrintStatement ParsePrintStatement(Print_statementContext node)
        {
            PrintStatement statement = new PrintStatement();

            statement.Content = new TokenInfo(node.expression());

            return statement;
        }

        public CallStatement ParseExecuteStatement(Execute_statementContext node)
        {
            CallStatement statement = new CallStatement();

            Execute_bodyContext body = node.execute_body();

            statement.Name = new TokenInfo(body.func_proc_name_server_database_schema()) { Type = TokenType.RoutineName };

            Execute_statement_argContext[] args = body.execute_statement_arg();

            if (args != null && args.Length > 0)
            {
                foreach (Execute_statement_argContext arg in args)
                {
                    statement.Arguments.Add(new TokenInfo(arg));
                }
            }

            return statement;
        }

        public TransactionStatement ParseTransactionStatment(Transaction_statementContext node)
        {
            TransactionStatement statement = new TransactionStatement();

            statement.Content = new TokenInfo(node);

            foreach (var child in node.children)
            {
                if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    if (type == TSqlParser.BEGIN)
                    {
                        statement.CommandType = TransactionCommandType.BEGIN;
                    }
                    else if (type == TSqlParser.COMMIT)
                    {
                        statement.CommandType = TransactionCommandType.COMMIT;
                    }
                    else if (type == TSqlParser.ROLLBACK)
                    {
                        statement.CommandType = TransactionCommandType.ROLLBACK;
                    }
                }
            }

            return statement;
        }

        public Statement ParseCursorStatement(Cursor_statementContext node)
        {
            Statement statement = null;

            bool isOpen = false;
            bool isClose = false;
            bool isDeallocate = false;

            foreach (var child in node.children)
            {
                if (child is Declare_cursorContext declare)
                {
                    statement = this.ParseDeclareCursor(declare);
                }
                else if (child is TerminalNodeImpl terminalNode)
                {
                    int type = terminalNode.Symbol.Type;

                    if (type == TSqlParser.OPEN)
                    {
                        isOpen = true;
                    }
                    else if (type == TSqlParser.CLOSE)
                    {
                        isClose = true;
                    }
                    else if (type == TSqlParser.DEALLOCATE)
                    {
                        isDeallocate = true;
                    }
                }
                else if (child is Cursor_nameContext name)
                {
                    if (isOpen)
                    {
                        OpenCursorStatement openCursorStatement = new OpenCursorStatement();
                        openCursorStatement.CursorName = new TokenInfo(name) { Type = TokenType.CursorName };

                        statement = openCursorStatement;
                    }
                    else if (isClose)
                    {
                        CloseCursorStatement closeCursorStatement = new CloseCursorStatement();
                        closeCursorStatement.CursorName = new TokenInfo(name) { Type = TokenType.CursorName };

                        statement = closeCursorStatement;
                    }
                    else if (isDeallocate)
                    {
                        DeallocateCursorStatement deallocateCursorStatement = new DeallocateCursorStatement();
                        deallocateCursorStatement.CursorName = new TokenInfo(name) { Type = TokenType.CursorName };

                        statement = deallocateCursorStatement;
                    }
                }
                else if (child is Fetch_cursorContext fetch)
                {
                    FetchCursorStatement fetchCursorStatement = new FetchCursorStatement();

                    fetchCursorStatement.CursorName = new TokenInfo(fetch.cursor_name()) { Type = TokenType.CursorName };

                    foreach (var fc in fetch.children)
                    {
                        if (fc is TerminalNodeImpl tn)
                        {
                            string text = tn.GetText();

                            if (text.StartsWith("@"))
                            {
                                fetchCursorStatement.Variables.Add(new TokenInfo(tn) { Type = TokenType.VariableName });
                            }
                        }
                    }

                    statement = fetchCursorStatement;
                }
            }

            return statement;
        }

        public DeclareCursorStatement ParseDeclareCursor(Declare_cursorContext node)
        {
            DeclareCursorStatement statement = new DeclareCursorStatement();
            statement.CursorName = new TokenInfo(node.cursor_name()) { Type = TokenType.CursorName };
            statement.SelectStatement = this.ParseSelectStatement(node.declare_set_cursor_common().select_statement()).FirstOrDefault();

            return statement;
        }

        private TokenInfo ParseCondition(ParserRuleContext node)
        {
            if (node != null)
            {
                if (node is Search_conditionContext || node is Search_condition_listContext)
                {
                    return this.ParseToken(node, TokenType.Condition);
                }
            }

            return null;
        }

        public override TableName ParseTableName(ParserRuleContext node, bool strict = false)
        {
            TableName tableName = null;

            if (node != null)
            {
                if (node is Table_nameContext tn)
                {
                    tableName = new TableName(tn);
                }
                else if (node is Full_table_nameContext fullName)
                {
                    tableName = new TableName(fullName);

                    tableName.Name = new TokenInfo(fullName.table);

                }
                else if (node is Table_source_itemContext tsi)
                {
                    tableName = new TableName(tsi);

                    tableName.Name = new TokenInfo(tsi.table_name_with_hint());

                    As_table_aliasContext alias = tsi.as_table_alias();

                    if (alias != null)
                    {
                        tableName.Alias = new TokenInfo(alias.table_alias());
                    }
                }
                else if (node is Table_sourcesContext tss)
                {
                    tableName = this.ParseTableName(tss.table_source().FirstOrDefault()?.table_source_item_joined()?.table_source_item());
                }
                else if (node is Table_sourceContext ts)
                {
                    tableName = this.ParseTableName(ts.table_source_item_joined()?.table_source_item());
                }
                else if (node is Ddl_objectContext ddl)
                {
                    return this.ParseTableName(ddl.full_table_name(), strict);
                }

                if (!strict && tableName == null)
                {
                    tableName = new TableName(node);
                }
            }

            return tableName;
        }

        public override ColumnName ParseColumnName(ParserRuleContext node, bool strict = false)
        {
            ColumnName columnName = null;

            if (node != null)
            {
                if (node is Full_column_nameContext fullName)
                {
                    columnName = new ColumnName(fullName);

                    IdContext id = fullName.id();
                    columnName.Name = new TokenInfo(id);

                    Table_nameContext tableName = fullName.table_name();

                    if (tableName != null)
                    {
                        columnName.TableName = new TokenInfo(tableName);
                    }
                }
                else if (node is Column_def_table_constraintContext col)
                {
                    columnName = new ColumnName(col);

                    columnName.Name = new TokenInfo(col.column_definition().id().First());
                    columnName.DataType = new TokenInfo(col.column_definition().data_type()) { Type = TokenType.DataType };
                }
                else if (node is Select_list_elemContext elem)
                {
                    AsteriskContext asterisk = elem.asterisk();

                    if (asterisk != null)
                    {
                        return this.ParseColumnName(asterisk, strict);
                    }

                    var columnEle = elem.column_elem();
                    var expEle = elem.expression_elem();

                    if (columnEle != null)
                    {
                        columnName = new ColumnName(columnEle);

                        columnName.Name = new TokenInfo(columnEle.id());

                        Table_nameContext tableName = columnEle.table_name();
                        var alias = columnEle.as_column_alias()?.column_alias();

                        if (tableName != null)
                        {
                            columnName.TableName = new TokenInfo(tableName);
                        }

                        if (alias != null)
                        {
                            columnName.Alias = new TokenInfo(alias);
                        }
                    }
                    else if (expEle != null)
                    {
                        return this.ParseColumnName(expEle, strict);
                    }
                }
                else if (node is AsteriskContext asterisk)
                {
                    columnName = new ColumnName(asterisk);

                    foreach (var ac in asterisk.children)
                    {
                        if (ac is TerminalNodeImpl terminalNode)
                        {
                            if (terminalNode.Symbol.Type == TSqlParser.STAR)
                            {
                                columnName.Name = new TokenInfo(terminalNode);
                                break;
                            }
                        }
                    }

                    Table_nameContext tableName = asterisk.table_name();

                    if (columnName != null && tableName != null)
                    {
                        columnName.TableName = new TokenInfo(tableName);
                    }
                }
                else if (node is Expression_elemContext expElem)
                {
                    columnName = new ColumnName(expElem);
                    columnName.Name = new TokenInfo(expElem.expression());

                    columnName.Tokens.AddRange(this.ParseToken(expElem, TokenType.ColumnName, true).Tokens);

                    Column_aliasContext alias = expElem.as_column_alias()?.column_alias();

                    if (alias != null)
                    {
                        columnName.Alias = new TokenInfo(alias);
                    }
                }
                else if (node is ExpressionContext exp)
                {
                    Full_column_nameContext fullColName = exp.full_column_name();

                    if (fullColName != null)
                    {
                        return this.ParseColumnName(fullColName, strict);
                    }
                }
                //else if(node is Column_aliasContext colAlias)
                //{
                //    if(colAlias.Parent!=null && colAlias.Parent is Column_alias_listContext)
                //    {
                //        columnName = new ColumnName(colAlias);
                //        columnName.Name = new TokenInfo(colAlias.id());
                //    }
                //}

                if (!strict && columnName == null)
                {
                    columnName = new ColumnName(node);
                }

                if (columnName?.Symbol?.Contains("=") == true)
                {
                    columnName.Tokens.AddRange(this.ParseToken(node, TokenType.RoutineName, true).Tokens);
                }
            }

            return columnName;
        }

        public override bool IsFunction(IParseTree node)
        {
            if (node is Function_callContext)
            {
                return true;
            }

            return false;
        }

        public override List<TokenInfo> GetTableNameTokens(IParseTree node)
        {
            List<TokenInfo> tokens = new List<TokenInfo>();

            TableName tableName = this.ParseTableName(node as ParserRuleContext, true);

            if (tableName != null)
            {
                tokens.Add(tableName);
            }

            return tokens;
        }

        public override List<TokenInfo> GetColumnNameTokens(IParseTree node)
        {
            List<TokenInfo> tokens = new List<TokenInfo>();

            ColumnName columnName = this.ParseColumnName(node as ParserRuleContext, true);

            if (columnName != null)
            {
                tokens.Add(columnName);
            }

            return tokens;
        }

        public override List<TokenInfo> GetRoutineNameTokens(IParseTree node)
        {
            List<TokenInfo> tokens = new List<TokenInfo>();

            ParserRuleContext routineName = null;

            if (node is Func_proc_name_server_database_schemaContext proc && proc.Parent.GetType() == typeof(Execute_bodyContext))
            {
                routineName = proc;
            }
            else if (node is Scalar_function_nameContext sfn)
            {
                IdContext[] ids = sfn.func_proc_name_server_database_schema()?.func_proc_name_database_schema()?.func_proc_name_schema()?.id();

                if (ids != null && ids.Length > 1)
                {
                    routineName = sfn;
                }
            }

            if (routineName != null)
            {
                tokens.Add(new TokenInfo(routineName) { Type = TokenType.RoutineName });
            }

            return tokens;
        }
    }
}
