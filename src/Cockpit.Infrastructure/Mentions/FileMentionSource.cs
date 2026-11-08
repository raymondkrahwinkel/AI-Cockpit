using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mentions;

namespace Cockpit.Infrastructure.Mentions;

internal sealed class FileMentionSource(IMentionFileSource files) : IMentionSource, ISingletonService
{
    public string Key => "files";

    public int Order => 0;

    public async Task<IReadOnlyList<MentionGroup>> GetAsync(MentionContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.WorkingDirectory))
        {
            return [];
        }

        var paths = await files.GetPathsAsync(context.WorkingDirectory, cancellationToken);
        var items = paths.Select(path =>
        {
            var name = path.TrimEnd('/');
            name = name[(name.LastIndexOf('/') + 1)..];
            return new MentionItem(
                path, name, path, new MentionTile(name.Length > 0 ? name[..1].ToUpperInvariant() : "?"), MentionKind.File, path, path);
        }).ToList();

        var folder = Path.GetFileName(context.WorkingDirectory.TrimEnd('/', '\\'));
        return [new MentionGroup("Files in " + folder, items)];
    }
}
