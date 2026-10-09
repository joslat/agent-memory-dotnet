using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Whether the message a preference was stored from says it (dreaming, <see cref="ConsolidationOptions.CloseUnsaidPreferences"/>).
/// The LLM extraction package registers one that asks the host's chat model; consolidation skips the operation, with a
/// warning, when none is registered.
/// </summary>
[Experimental("AMDREAM001")]
public interface IPreferenceSourceCheck
{
    /// <summary>
    /// The keys of the preferences the message does not say: read into it rather than said (a trait, a liking, a
    /// generalisation from one remark, someone else's preference). A preference said in part is not returned; neither is
    /// one the answer leaves out, so a failed or unreadable answer closes nothing.
    /// </summary>
    Task<IReadOnlyCollection<string>> FindUnsaidAsync(PreferenceSourceRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One source message and the live preferences stored from it.</summary>
/// <param name="Message">What the person said.</param>
/// <param name="SaidAt">When it was said: relative dates in the stored preferences are read against it.</param>
/// <param name="Earlier">Up to two of the person's messages just before it in the same conversation, oldest first.</param>
/// <param name="Preferences">The preferences stored from it.</param>
[Experimental("AMDREAM001")]
public sealed record PreferenceSourceRequest(
    string Message, DateTimeOffset SaidAt, IReadOnlyList<string> Earlier, IReadOnlyList<PreferenceSourceItem> Preferences);

/// <summary>A stored preference, keyed for the answer.</summary>
[Experimental("AMDREAM001")]
public sealed record PreferenceSourceItem(string Key, string? Category, string Text);
