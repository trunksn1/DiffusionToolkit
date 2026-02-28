using System;
using System.Collections.Generic;
using System.Linq;

namespace Diffusion.Toolkit.Models;

public static class SmartAlbumRuleHelper
{
    public static List<SmartAlbumField> AllFields { get; } =
        Enum.GetValues<SmartAlbumField>().ToList();

    public static List<SmartAlbumOperator> AllOperators { get; } =
        Enum.GetValues<SmartAlbumOperator>().ToList();
}
