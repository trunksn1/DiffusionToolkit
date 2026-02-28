using System;
using System.Collections.Generic;
using System.Text.Json;
using Diffusion.Toolkit.Models;

namespace Diffusion.Toolkit.Services;

public class SmartAlbumEvaluator
{
    public static (string WhereClause, Dictionary<string, object> Parameters) BuildQuery(
        List<SmartAlbumRule> rules, bool matchAll)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object>();
        int paramIndex = 0;

        foreach (var rule in rules)
        {
            var (clause, ruleParams) = BuildRuleClause(rule, paramIndex);
            if (clause != null)
            {
                clauses.Add(clause);
                foreach (var p in ruleParams)
                {
                    parameters[p.Key] = p.Value;
                }
            }
            paramIndex++;
        }

        if (clauses.Count == 0)
            return ("1=1", parameters);

        var connector = matchAll ? " AND " : " OR ";
        return (string.Join(connector, clauses), parameters);
    }

    private static (string? Clause, Dictionary<string, object> Parameters) BuildRuleClause(
        SmartAlbumRule rule, int index)
    {
        var parameters = new Dictionary<string, object>();
        var paramName = $"@p{index}";
        var column = GetColumnName(rule.Field);

        if (column == null)
            return (null, parameters);

        string clause;

        switch (rule.Operator)
        {
            case SmartAlbumOperator.Equals:
                if (rule.Field is SmartAlbumField.Favorite or SmartAlbumField.NSFW or SmartAlbumField.ForDeletion)
                {
                    var boolVal = rule.Value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                  rule.Value == "1";
                    parameters[paramName] = boolVal ? 1 : 0;
                }
                else
                {
                    parameters[paramName] = ParseValue(rule);
                }
                clause = $"{column} = {paramName}";
                break;

            case SmartAlbumOperator.NotEquals:
                parameters[paramName] = ParseValue(rule);
                clause = $"{column} != {paramName}";
                break;

            case SmartAlbumOperator.GreaterThan:
                parameters[paramName] = ParseValue(rule);
                clause = $"{column} > {paramName}";
                break;

            case SmartAlbumOperator.LessThan:
                parameters[paramName] = ParseValue(rule);
                clause = $"{column} < {paramName}";
                break;

            case SmartAlbumOperator.GreaterThanOrEqual:
                parameters[paramName] = ParseValue(rule);
                clause = $"{column} >= {paramName}";
                break;

            case SmartAlbumOperator.LessThanOrEqual:
                parameters[paramName] = ParseValue(rule);
                clause = $"{column} <= {paramName}";
                break;

            case SmartAlbumOperator.Contains:
                parameters[paramName] = $"%{rule.Value}%";
                clause = $"{column} LIKE {paramName}";
                break;

            case SmartAlbumOperator.NotContains:
                parameters[paramName] = $"%{rule.Value}%";
                clause = $"({column} NOT LIKE {paramName} OR {column} IS NULL)";
                break;

            case SmartAlbumOperator.InLastNDays:
                if (int.TryParse(rule.Value, out var days))
                {
                    var cutoffDate = DateTime.Now.AddDays(-days);
                    parameters[paramName] = cutoffDate;
                    clause = $"{column} >= {paramName}";
                }
                else
                {
                    return (null, parameters);
                }
                break;

            default:
                return (null, parameters);
        }

        return (clause, parameters);
    }

    private static string? GetColumnName(SmartAlbumField field)
    {
        return field switch
        {
            SmartAlbumField.Rating => "Rating",
            SmartAlbumField.Favorite => "Favorite",
            SmartAlbumField.NSFW => "NSFW",
            SmartAlbumField.Model => "Model",
            SmartAlbumField.Sampler => "Sampler",
            SmartAlbumField.Prompt => "Prompt",
            SmartAlbumField.NegativePrompt => "NegativePrompt",
            SmartAlbumField.CreatedDate => "CreatedDate",
            SmartAlbumField.Width => "Width",
            SmartAlbumField.Height => "Height",
            SmartAlbumField.FileSize => "FileSize",
            SmartAlbumField.Steps => "Steps",
            SmartAlbumField.CFGScale => "CFGScale",
            SmartAlbumField.ForDeletion => "ForDeletion",
            _ => null
        };
    }

    private static object ParseValue(SmartAlbumRule rule)
    {
        if (decimal.TryParse(rule.Value, out var decVal))
            return decVal;
        return rule.Value;
    }

    public static List<SmartAlbumRule> DeserializeRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SmartAlbumRule>>(json) ?? new List<SmartAlbumRule>();
        }
        catch
        {
            return new List<SmartAlbumRule>();
        }
    }

    public static string SerializeRules(List<SmartAlbumRule> rules)
    {
        return JsonSerializer.Serialize(rules);
    }
}
