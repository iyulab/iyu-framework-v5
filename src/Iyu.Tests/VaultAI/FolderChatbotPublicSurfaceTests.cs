using System.Reflection;
using Iyu.VaultAI.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The folder chatbots are not part of the published surface yet. A public type published here would be an API nobody can use (the
/// registration is internal), and a public API cannot be taken back, so nothing of them is exported until they are opened on purpose.
/// </summary>
public class FolderChatbotPublicSurfaceTests
{
    private static readonly Assembly VaultAI = typeof(FolderChatbotModelPicker).Assembly;

    [Fact]
    public void No_type_in_the_folder_chatbot_namespace_is_exported()
    {
        var exported = VaultAI.GetExportedTypes()
            .Where(t => string.Equals(t.Namespace, "Iyu.VaultAI.FolderChatbots", StringComparison.Ordinal)
                || t.Namespace?.StartsWith("Iyu.VaultAI.FolderChatbots.", StringComparison.Ordinal) == true)
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(exported);
    }

    [Fact]
    public void No_exported_type_or_member_is_named_for_the_folder_chatbots()
    {
        var named = new List<string>();
        foreach (var type in VaultAI.GetExportedTypes())
        {
            if (type.Name.Contains("FolderChatbot", StringComparison.Ordinal))
            {
                named.Add(type.FullName!);
            }

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
}
