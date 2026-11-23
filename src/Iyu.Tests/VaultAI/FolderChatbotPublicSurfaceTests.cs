using System.Reflection;
using Iyu.VaultAI;
using Iyu.VaultAI.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// What the folder chatbots publish: one registration method and the two classes its settings bind to - nothing else. A public API
/// cannot be taken back, so the engine's seams (the vault-ai admin client, the folder source, the manifest store, the sync service, the
/// status reader) stay internal, and adding one to this list is a decision made here on purpose, not a side effect of marking a class public.
/// </summary>
public class FolderChatbotPublicSurfaceTests
{
    private static readonly Assembly VaultAI = typeof(VaultAIFolderChatbotExtensions).Assembly;

    private static readonly string[] PublicTypes =
    [
        typeof(VaultAIFolderChatbotExtensions).FullName!,
        typeof(VaultAIFolderChatbotOptions).FullName!,
        typeof(VaultAIFolderChatbotModelOptions).FullName!,
    ];

    [Fact]
    public void The_folder_chatbots_export_the_registration_and_the_two_settings_classes_and_nothing_else()
    {
        var exported = VaultAI.GetExportedTypes()
            .Where(t => t.Namespace is { } ns && (ns == "Iyu.VaultAI.FolderChatbots" || ns.StartsWith("Iyu.VaultAI.FolderChatbots.", StringComparison.Ordinal))
                || t.Name.Contains("FolderChatbot", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(PublicTypes.Order(StringComparer.Ordinal), exported);
    }

    [Fact]
    public void The_registration_is_one_extension_method_on_the_service_collection()
    {
        var methods = typeof(VaultAIFolderChatbotExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        var method = Assert.Single(methods);
        Assert.Equal("AddVaultAIFolderChatbots", method.Name);
        Assert.True(method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), inherit: false));
    }

    [Fact]
    public void No_other_exported_member_is_named_for_the_folder_chatbots()
    {
        var named = new List<string>();
        foreach (var type in VaultAI.GetExportedTypes().Where(t => !PublicTypes.Contains(t.FullName!)))
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member.Name.Contains("FolderChatbot", StringComparison.Ordinal))
                {
                    named.Add($"{type.FullName}.{member.Name}");
                }
            }
        }

        Assert.Empty(named);
    }

    [Fact]
    public void A_public_type_of_the_folder_chatbots_exposes_no_internal_type()
    {
        // A public member whose type a consumer cannot name is a hole in the surface, not a feature.
        var leaks = new List<string>();
        foreach (var type in PublicTypes.Select(n => VaultAI.GetType(n)!))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                if (!property.PropertyType.IsVisible) leaks.Add($"{type.Name}.{property.Name}");
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (!method.ReturnType.IsVisible || method.GetParameters().Any(p => !p.ParameterType.IsVisible)) leaks.Add($"{type.Name}.{method.Name}");
        }

        Assert.Empty(leaks);
    }
}
