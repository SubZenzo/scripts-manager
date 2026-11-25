using System.Text;

namespace ScriptsManager.Core.Authoring;

/// <summary>Переменная из <c>DECLARE</c> в начале T-SQL скрипта — кандидат в параметры.</summary>
/// <param name="SqlType">Тип как в скрипте, например <c>varchar(50)</c>.</param>
/// <param name="Initializer">Выражение после <c>=</c>; null — без значения.</param>
public sealed record TSqlDeclaration(string Name, string SqlType, string? Initializer);

/// <summary>
/// Объявления переменных в начале T-SQL скрипта (до первого оператора, отличного от DECLARE и SET-опций).
/// Переменные, перенесённые в параметры, нужно убрать из скрипта: приложение передаёт их само,
/// и повторное объявление — ошибка.
/// </summary>
public static class TSqlDeclarations
{
    public static IReadOnlyList<TSqlDeclaration> Find(string text) =>
        Parse(text).SelectMany(s => s.Items).Where(i => !i.IsTable).Select(i => i.Declaration).ToList();

    /// <summary>Убирает из начальных DECLARE переменные <paramref name="names"/> (без учёта регистра).</summary>
    public static string Remove(string text, IEnumerable<string> names)
    {
        var remove = names.Select(n => n.TrimStart('@')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder(text);

        // С конца, чтобы не сдвигались позиции.
        foreach (var statement in Parse(text).AsEnumerable().Reverse())
        {
            var kept = statement.Items.Where(i => i.IsTable || !remove.Contains(i.Declaration.Name)).ToList();
            if (kept.Count == statement.Items.Count)
                continue;

            if (kept.Count == 0)
            {
                var end = statement.End;
                // Вместе с хвостом строки, если после оператора на ней ничего нет.
                var e = end;
                while (e < text.Length && text[e] is ' ' or '\t')
                    e++;
                if (e < text.Length && text[e] == '\r')
                    e++;
                if (e < text.Length && text[e] == '\n')
                    end = e + 1;
                else if (e == text.Length)
                    end = e;
                result.Remove(statement.Start, end - statement.Start);
            }
            else
            {
                var first = statement.Items[0].Start;
                var last = statement.Items[^1].End;
                result.Remove(first, last - first);
                result.Insert(first, string.Join(", ", kept.Select(i => text[i.Start..i.End])));
            }
        }

        return result.ToString();
    }

    private sealed record Item(TSqlDeclaration Declaration, int Start, int End, bool IsTable);

    private sealed record Statement(int Start, int End, List<Item> Items);

    private static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "declare", "select", "set", "if", "while", "begin", "exec", "execute", "insert", "update", "delete", "with",
        "print", "raiserror", "use", "create", "alter", "drop", "truncate", "merge", "go", "return", "throw", "else",
        "end", "open", "fetch", "close", "deallocate", "waitfor", "commit", "rollback",
    };

    private static List<Statement> Parse(string text)
    {
        var tokens = TSqlLexer.Tokenize(text).Where(t => t.Kind is not (TokenKind.Space or TokenKind.Comment)).ToList();
        var statements = new List<Statement>();
        var i = 0;

        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t.IsWord("set") && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Word)
            {
                // SET NOCOUNT ON и подобные — до конца строки или «;».
                var line = LineOf(text, t.Start);
                i++;
                while (i < tokens.Count && LineOf(text, tokens[i].Start) == line && !tokens[i].IsSymbol(";"))
                    i++;
                if (i < tokens.Count && tokens[i].IsSymbol(";"))
                    i++;
                continue;
            }

            if (!t.IsWord("declare") || ParseDeclare(text, tokens, ref i) is not { } statement)
                break;
            statements.Add(statement);
        }

        return statements;
    }

    private static Statement? ParseDeclare(string text, List<Token> tokens, ref int i)
    {
        var start = tokens[i].Start;
        var p = i + 1;
        var items = new List<Item>();

        while (true)
        {
            if (p >= tokens.Count || tokens[p].Kind != TokenKind.Variable)
                return null; // курсор или что-то непонятное — дальше не разбираем
            var variable = tokens[p++];
            if (p < tokens.Count && tokens[p].IsWord("as"))
                p++;

            // Тип: имя (возможно составное и в скобках []) и аргументы в скобках.
            var typeStart = p;
            if (p >= tokens.Count || !IsName(tokens[p]))
                return null;
            p++;
            while (p + 1 < tokens.Count && tokens[p].IsSymbol(".") && IsName(tokens[p + 1]))
                p += 2;
            if (p < tokens.Count && tokens[p].IsSymbol("("))
                p = SkipParens(tokens, p);
            var typeText = text[tokens[typeStart].Start..tokens[p - 1].End];
            var isTable = tokens[typeStart].IsWord("table");

            string? initializer = null;
            if (!isTable && p < tokens.Count && tokens[p].IsSymbol("="))
            {
                p++;
                var exprStart = p;
                var depth = 0;
                var caseDepth = 0;
                while (p < tokens.Count)
                {
                    var t = tokens[p];
                    if (t.IsSymbol("(")) depth++;
                    else if (t.IsSymbol(")")) depth--;
                    else if (depth == 0 && (t.IsSymbol(",") || t.IsSymbol(";"))) break;
                    else if (depth == 0 && t.IsWord("case")) caseDepth++;
                    else if (depth == 0 && caseDepth > 0 && t.IsWord("end")) caseDepth--;
                    else if (depth == 0 && caseDepth == 0 && t.Kind == TokenKind.Word && StatementKeywords.Contains(t.Text)) break;
                    if (depth < 0) break;
                    p++;
                }
                if (p == exprStart)
                    return null;
                initializer = text[tokens[exprStart].Start..tokens[p - 1].End];
            }

            var declaration = new TSqlDeclaration(variable.Text[1..], typeText, initializer);
            items.Add(new Item(declaration, variable.Start, tokens[p - 1].End, isTable));

            if (p < tokens.Count && tokens[p].IsSymbol(","))
            {
                p++;
                continue;
            }
            break;
        }

        var end = tokens[p - 1].End;
        if (p < tokens.Count && tokens[p].IsSymbol(";"))
            end = tokens[p++].End;
        i = p;
        return new Statement(start, end, items);
    }

    private static bool IsName(Token t) => t.Kind is TokenKind.Word or TokenKind.Quoted;

    private static int SkipParens(List<Token> tokens, int p)
    {
        var depth = 0;
        do
        {
            if (tokens[p].IsSymbol("(")) depth++;
            else if (tokens[p].IsSymbol(")")) depth--;
            p++;
        } while (p < tokens.Count && depth > 0);
        return p;
    }

    private static int LineOf(string text, int position)
    {
        var line = 0;
        for (var k = 0; k < position; k++)
            if (text[k] == '\n')
                line++;
        return line;
    }
}

internal enum TokenKind { Space, Comment, String, Quoted, Word, Variable, Number, Symbol }

internal readonly record struct Token(TokenKind Kind, int Start, int End, string Text)
{
    public bool IsWord(string word) => Kind == TokenKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
    public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;
}

/// <summary>Простой лексер T-SQL: комментарии, строки, [имена], @переменные.</summary>
internal static class TSqlLexer
{
    public static IEnumerable<Token> Tokenize(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var c = text[i];
            TokenKind kind;

            if (char.IsWhiteSpace(c))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                kind = TokenKind.Space;
            }
            else if (c == '-' && Next(text, i) == '-')
            {
                while (i < text.Length && text[i] != '\n') i++;
                kind = TokenKind.Comment;
            }
            else if (c == '/' && Next(text, i) == '*')
            {
                // Блочные комментарии в T-SQL вкладываются.
                var depth = 0;
                do
                {
                    if (text[i] == '/' && Next(text, i) == '*') { depth++; i += 2; }
                    else if (text[i] == '*' && Next(text, i) == '/') { depth--; i += 2; }
                    else i++;
                } while (i < text.Length && depth > 0);
                kind = TokenKind.Comment;
            }
            else if (c == '\'' || (c is 'N' or 'n' && Next(text, i) == '\''))
            {
                i = SkipQuoted(text, c == '\'' ? i : i + 1, '\'');
                kind = TokenKind.String;
            }
            else if (c is '[' or '"')
            {
                i = SkipQuoted(text, i, c == '[' ? ']' : '"');
                kind = TokenKind.Quoted;
            }
            else if (c == '@' || char.IsLetter(c) || c is '_' or '#')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '#' or '$')) i++;
                kind = c == '@' ? TokenKind.Variable : TokenKind.Word;
            }
            else if (char.IsDigit(c) || (c == '.' && char.IsDigit(Next(text, i))))
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.')) i++;
                kind = TokenKind.Number;
            }
            else
            {
                i++;
                kind = TokenKind.Symbol;
            }

            yield return new Token(kind, start, i, text[start..i]);
        }
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    /// <summary>Конец строки в кавычках; удвоенная закрывающая кавычка — экранирование.</summary>
    private static int SkipQuoted(string text, int i, char close)
    {
        i++;
        while (i < text.Length)
        {
            if (text[i] == close)
            {
                if (Next(text, i) == close) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return i;
    }
}
