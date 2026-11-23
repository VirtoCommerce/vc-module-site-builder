namespace VirtoCommerce.SiteBuilder.Data.Theme;

/// <param name="HasCatalog">A catalog was given: without one, nothing is known of the kit.</param>
/// <param name="Tags">The attributes of each kit tag, by its name; <c>null</c> for a catalog without a kit.</param>
/// <param name="Behaviors">Each behavior by its name; <c>null</c> for a catalog without a kit.</param>
/// <param name="Icons">The storefront's icon names; <c>null</c> when the kit lists none, and an icon name is not checked.</param>
public sealed record StorefrontKit(bool HasCatalog, IReadOnlyDictionary<string, IReadOnlyDictionary<string, KitField>> Tags,
    IReadOnlyDictionary<string, KitBehavior> Behaviors, IReadOnlySet<string> Icons)
{
    /// <summary>By kit tag: its first attribute the server cannot check, named with why; a stored catalog may hold one its upload would refuse now.</summary>
    public IReadOnlyDictionary<string, string> TagFaults { get; } = (Tags ?? new Dictionary<string, IReadOnlyDictionary<string, KitField>>())
        .Select(x => KeyValuePair.Create(x.Key, KitField.GetFirstFault(x.Value)))
        .Where(x => x.Value != null)
        .ToDictionary(StringComparer.Ordinal);

    /// <summary>The solid icon names vc-shape masks with; <c>null</c> when the kit lists none, and a mask name is not checked.</summary>
    public IReadOnlySet<string> Masks { get; init; }

    /// <summary>The keys of the catalog's templates, which a type's targetTemplates name; <c>null</c> when it lists none, and they are not checked.</summary>
    public IReadOnlySet<string> TemplateKeys { get; init; }

    /// <summary>The palette's tokens the catalog lists, light and dark; <c>null</c> when it lists none, and a token is not checked.</summary>
    public IReadOnlySet<string> PaletteTokens { get; init; }

    /// <summary>The kit tags whose copy would repeat their work, such as a request; none inside an element whose behavior repeats what it holds.</summary>
    public IReadOnlySet<string> SideEffectTags { get; init; }

    /// <summary>By kit tag: its cost, the most elements one of it renders, which the render budget counts; a tag that declares none costs 1.</summary>
    public IReadOnlyDictionary<string, int> TagCosts { get; init; }

    /// <summary>The longest behavior name, beyond which no prefix of an attribute can name a behavior.</summary>
    public int MaxBehaviorLength { get; } = Behaviors?.Keys.Select(x => x.Length).DefaultIfEmpty().Max() ?? 0;
}

/// <param name="Type">string, number, boolean, select, date or link; any other refuses the kit tag or the behavior that declares it.</param>
/// <param name="OptionsFrom">Where a select's values are listed instead of its options: "icons" for the kit's icon names, "masks" for its mask names.</param>
public sealed record KitField(string Type, IReadOnlyList<string> Options, decimal? Min, decimal? Max, string OptionsFrom = null)
{
    /// <summary>Why the server cannot check a value of the field, to follow its name; <c>null</c> when it can.</summary>
    public string Fault { get; } = (Type, Options, OptionsFrom) switch
    {
        _ when !CustomSectionKit.FieldTypes.Contains(Type ?? string.Empty) => $" of a type the server does not know, '{Type}'",
        (CustomSectionKit.SelectType, null, null) => ", a select with neither options nor optionsFrom",
        (CustomSectionKit.SelectType, null, not (CustomSectionKit.IconsKey or CustomSectionKit.MasksKey)) =>
            $", a select with no options and an optionsFrom other than \"{CustomSectionKit.IconsKey}\" or \"{CustomSectionKit.MasksKey}\", '{OptionsFrom}'",
        _ => null,
    };

    /// <returns>The first field the server cannot check, as 'name' and why; <c>null</c> for none.</returns>
    public static string GetFirstFault(IEnumerable<KeyValuePair<string, KitField>> fields) =>
        fields.Where(x => x.Value.Fault != null).Select(x => $"'{x.Key}'{x.Value.Fault}").FirstOrDefault();
}

/// <param name="Value">The type of the behavior attribute's own value; <c>null</c> when it declares none.</param>
/// <param name="RepeatsChildren">The behavior copies what its element holds, as a marquee does, so it may not stand inside another that does.</param>
public sealed record KitBehavior(KitField Value, IReadOnlyDictionary<string, KitField> Options, bool RepeatsChildren = false)
{
    /// <summary>
    /// The attributes of the elements whose text the behavior writes, its own data-{name} or an option's: such an option goes on the element with
    /// the behavior or inside it, and any other option on the element with the behavior; <c>null</c> for none.
    /// </summary>
    public IReadOnlySet<string> TextAttributes { get; init; }

    /// <summary>Its first option or value the server cannot check, named with why; a stored catalog may hold one its upload would refuse now.</summary>
    public string Fault { get; } = KitField.GetFirstFault(Value == null ? Options : Options.Append(KeyValuePair.Create("value", Value)));
}
