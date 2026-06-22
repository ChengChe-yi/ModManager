using System;
using System.Collections.Generic;
using System.Linq;

namespace ModManager.Core.Search
{
    // ============================================================
    // 搜索模型
    // ============================================================

    /// <summary>Token 类型：标签匹配 or 名称匹配</summary>
    public enum TokenType
    {
        /// <summary>匹配已知标签值</summary>
        Tag,
        /// <summary>匹配角色名 / Mod 名</summary>
        Name
    }

    /// <summary>单个搜索词</summary>
    public class SearchToken
    {
        public string Value { get; }
        public TokenType Type { get; }

        public SearchToken(string value, TokenType type)
        {
            Value = value;
            Type = type;
        }

        public override string ToString() => Type == TokenType.Tag ? $"tag:{Value}" : Value;
    }

    /// <summary>AND 组：组内所有 Token 必须同时匹配（空格连接）</summary>
    public class AndGroup
    {
        public List<SearchToken> Tokens { get; } = new();

        /// <summary>该组中所有标签 Token</summary>
        public IEnumerable<SearchToken> TagTokens => Tokens.Where(t => t.Type == TokenType.Tag);

        /// <summary>该组中所有名称 Token</summary>
        public IEnumerable<SearchToken> NameTokens => Tokens.Where(t => t.Type == TokenType.Name);

        /// <summary>该组是否包含任何 Token</summary>
        public bool HasTokens => Tokens.Count > 0;

        public override string ToString() => Tokens.Count > 0
            ? string.Join(" + ", Tokens)
            : "<空>";
    }

    /// <summary>完整的搜索表达式：多个 AndGroup 之间为 OR 关系（| 连接）</summary>
    public class SearchExpression
    {
        public List<AndGroup> OrGroups { get; } = new();

        /// <summary>是否为空（无搜索条件）</summary>
        public bool IsEmpty => OrGroups.Count == 0 || OrGroups.All(g => !g.HasTokens);

        public override string ToString() => IsEmpty
            ? "<空查询>"
            : string.Join(" | ", OrGroups);
    }

    // ============================================================
    // 解析器
    // ============================================================

    /// <summary>
    /// 角色搜索查询解析器。
    /// 语法：空格 = AND，竖线 | = OR。
    /// 每个 Token 会自动分类为 Tag（匹配已知标签值）或 Name（匹配角色名/Mod 名）。
    /// </summary>
    public static class TagSearchParser
    {
        /// <summary>
        /// 解析搜索查询字符串。
        /// </summary>
        /// <param name="query">原始搜索输入（例："雷 长枪|胡桃"）</param>
        /// <param name="knownTags">标签全集（从所有 info.json 收集的所有标签值，大小写不敏感比较）</param>
        /// <returns>结构化的 SearchExpression</returns>
        public static SearchExpression Parse(string query, ISet<string>? knownTags)
        {
            var expr = new SearchExpression();
            var tagSet = knownTags ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(query))
                return expr;

            // Step 1: 按 | 拆分为 OR 组
            string[] orParts = query.Split('|', StringSplitOptions.RemoveEmptyEntries);

            foreach (string orPart in orParts)
            {
                var group = new AndGroup();

                // Step 2: 按空格拆分为 AND Token
                string[] andTokens = orPart.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

                foreach (string rawToken in andTokens)
                {
                    string value = rawToken.Trim();
                    if (string.IsNullOrEmpty(value))
                        continue;

                    // Step 3: 判断是否匹配已知标签
                    bool isTag = tagSet.Contains(value);
                    var token = new SearchToken(value, isTag ? TokenType.Tag : TokenType.Name);
                    group.Tokens.Add(token);
                }

                if (group.HasTokens)
                    expr.OrGroups.Add(group);
            }

            return expr;
        }

        /// <summary>
        /// 检查给定名称和标签集合是否匹配表达式。
        /// </summary>
        /// <param name="displayName">角色/Mod 显示名称</param>
        /// <param name="itemTags">该项的标签列表</param>
        /// <param name="expression">已解析的搜索表达式</param>
        /// <returns>是否匹配</returns>
        public static bool IsMatch(string displayName, IReadOnlyList<string> itemTags, SearchExpression expression)
        {
            if (expression.IsEmpty)
                return true;

            // OR 语义：任一 AndGroup 匹配即通过
            return expression.OrGroups.Any(group => IsAndGroupMatch(displayName, itemTags, group));
        }

        private static bool IsAndGroupMatch(string displayName, IReadOnlyList<string> itemTags, AndGroup group)
        {
            if (!group.HasTokens)
                return true;

            var itemTagSet = new HashSet<string>(itemTags, StringComparer.OrdinalIgnoreCase);

            // AND 语义：所有 Token 都必须匹配
            foreach (var token in group.Tokens)
            {
                bool matched = token.Type switch
                {
                    TokenType.Tag => itemTagSet.Contains(token.Value),
                    TokenType.Name => displayName.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (!matched)
                    return false;
            }

            return true;
        }
    }
}
