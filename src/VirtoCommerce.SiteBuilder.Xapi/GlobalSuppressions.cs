using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Critical Code Smell", "S1699:Constructors should only call non-overridable methods",
    Scope = "namespaceanddescendants", Target = "~N:VirtoCommerce.SiteBuilder.Xapi.Schemas",
    Justification = "graphql-dotnet schema types declare their fields in the constructor through the virtual Field method; they stay unsealed so that other modules can extend them.")]
