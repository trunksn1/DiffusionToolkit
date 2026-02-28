using System.Collections.Generic;

namespace Diffusion.Toolkit.Models;

public enum SmartAlbumField
{
    Rating,
    Favorite,
    NSFW,
    Model,
    Sampler,
    Prompt,
    NegativePrompt,
    CreatedDate,
    Width,
    Height,
    FileSize,
    Steps,
    CFGScale,
    ForDeletion
}

public enum SmartAlbumOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    GreaterThanOrEqual,
    LessThanOrEqual,
    Contains,
    NotContains,
    InLastNDays
}

public class SmartAlbumRule
{
    public SmartAlbumField Field { get; set; }
    public SmartAlbumOperator Operator { get; set; }
    public string Value { get; set; } = "";

    public static List<SmartAlbumOperator> GetOperatorsForField(SmartAlbumField field)
    {
        return field switch
        {
            SmartAlbumField.Rating or SmartAlbumField.Width or SmartAlbumField.Height
                or SmartAlbumField.FileSize or SmartAlbumField.Steps or SmartAlbumField.CFGScale =>
                new List<SmartAlbumOperator>
                {
                    SmartAlbumOperator.Equals, SmartAlbumOperator.NotEquals,
                    SmartAlbumOperator.GreaterThan, SmartAlbumOperator.LessThan,
                    SmartAlbumOperator.GreaterThanOrEqual, SmartAlbumOperator.LessThanOrEqual
                },

            SmartAlbumField.Favorite or SmartAlbumField.NSFW or SmartAlbumField.ForDeletion =>
                new List<SmartAlbumOperator> { SmartAlbumOperator.Equals },

            SmartAlbumField.Model or SmartAlbumField.Sampler =>
                new List<SmartAlbumOperator>
                {
                    SmartAlbumOperator.Equals, SmartAlbumOperator.NotEquals,
                    SmartAlbumOperator.Contains
                },

            SmartAlbumField.Prompt or SmartAlbumField.NegativePrompt =>
                new List<SmartAlbumOperator>
                {
                    SmartAlbumOperator.Contains, SmartAlbumOperator.NotContains
                },

            SmartAlbumField.CreatedDate =>
                new List<SmartAlbumOperator> { SmartAlbumOperator.InLastNDays },

            _ => new List<SmartAlbumOperator> { SmartAlbumOperator.Equals }
        };
    }
}
