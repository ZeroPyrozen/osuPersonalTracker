using System.ComponentModel.DataAnnotations;

namespace OsuTracker.Web.Data.Entities;

/// <summary>Nearly empty by design — rules 3 to 5 are not configurable.</summary>
public class Setting
{
    [MaxLength(64)] public string Key { get; set; } = "";
    [MaxLength(1024)] public string Value { get; set; } = "";
}
