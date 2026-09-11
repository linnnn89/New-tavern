using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Compatibility;

namespace TavernDesk.Infrastructure.Storage;

/// <summary>Commits one imported card and its optional embedded worldbook together.</summary>
public sealed class SqliteCharacterImportStore(SqliteDatabase database, AppDataPaths paths)
{
    private readonly SqliteCharacterRepository _characters = new(database, paths);
    private readonly SqliteWorldbookRepository _worldbooks = new(database, paths);

    public async Task<CharacterCardImportReport> SaveAsync(
        Character character, CharacterCardImportReport report, WorldbookImportResult? worldbook,
        CancellationToken cancellationToken)
    {
        await using var connection = database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        if (worldbook is not null)
        {
            var imported = await _worldbooks.ImportAsync(worldbook, WorldbookScopeKind.Character,
                character.Id, connection, transaction, cancellationToken);
            report = report with { Warnings = report.Warnings.Concat(imported.Warnings).ToArray() };
        }
        character.ImportReportJson = CharacterCardReportSerializer.Write(report);
        await _characters.UpsertAsync(character, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return report;
    }
}
