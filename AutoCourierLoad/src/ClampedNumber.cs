using System;
using BepInEx.Configuration;

namespace AutoCourierLoad;

/// <summary>
/// Keeps a float setting between Min and Max, like <c>AcceptableValueRange&lt;float&gt;</c>, but without
/// the public <c>MinValue</c>/<c>MaxValue</c> properties. Config managers (Mod Settings Tool,
/// BepInExConfigManager) look for those properties to draw a slider; without them they draw a text
/// field you can type a number into, and BepInEx clamps whatever is typed through <see cref="Clamp"/>.
/// </summary>
internal sealed class ClampedFloat : AcceptableValueBase
{
    private readonly float _min;
    private readonly float _max;

    public ClampedFloat(float min, float max) : base(typeof(float))
    {
        _min = min;
        _max = max;
    }

    public override object Clamp(object value)
    {
        float v = value is float f ? f : Convert.ToSingle(value);
        if (float.IsNaN(v)) v = _min;
        return Math.Clamp(v, _min, _max);
    }

    // Any number but NaN is accepted so that it reaches Clamp: Mod Settings Tool checks IsValid before it
    // commits a typed value and silently restores the old one when IsValid is false.
    public override bool IsValid(object value) => value is float f && !float.IsNaN(f);

    public override string ToDescriptionString() => $"# Acceptable value range: From {_min} to {_max}";
}

/// <summary>The same for an integer setting (a text field instead of a slider, clamped on commit).</summary>
internal sealed class ClampedInt : AcceptableValueBase
{
    private readonly int _min;
    private readonly int _max;

    public ClampedInt(int min, int max) : base(typeof(int))
    {
        _min = min;
        _max = max;
    }

    public override object Clamp(object value)
    {
        int v = value is int i ? i : Convert.ToInt32(value);
        return Math.Clamp(v, _min, _max);
    }

    public override bool IsValid(object value) => value is int;

    public override string ToDescriptionString() => $"# Acceptable value range: From {_min} to {_max}";
}
