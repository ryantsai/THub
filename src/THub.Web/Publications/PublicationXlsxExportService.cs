using System.Globalization;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Localization;
using THub.Application.Publications;
using THub.Domain.Publications;
using THub.Web.Localization;

namespace THub.Web.Publications;

public sealed record PublicationXlsxExportResult(
    Stream? Stream,
    string? FileName,
    int StatusCode,
    string? Error);

public sealed class PublicationXlsxExportService(
    IPublicationCatalogStore catalogStore,
    IPublicationSourceDataReader sourceDataReader,
    PublicationAuthorizationService authorizationService,
    IStringLocalizer<SharedResource> localizer,
    ILogger<PublicationXlsxExportService> logger)
{
    private const long MaximumExcelDataRows = 1_048_575;
    private const int MaximumExcelColumns = 16_384;
    private const int MaximumExcelCellCharacters = 32_767;
    private const int ExportBatchSize = 1_000;

    public async Task<PublicationXlsxExportResult> CreateAsync(
        Guid publicationId,
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
                publicationId,
                roleIds,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess)
        {
            return Failure(
                StatusCodes.Status403Forbidden,
                localizer["You do not have View access to this table."]);
        }

        var publication = await catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (publication is null)
        {
            return Failure(StatusCodes.Status404NotFound, localizer["The publication was not found."]);
        }

        if (publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId is not Guid versionId)
        {
            return Failure(
                StatusCodes.Status409Conflict,
                localizer["XLSX export requires an active spreadsheet publication."]);
        }

        var version = await catalogStore.FindVersionAsync(
                publicationId,
                versionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (version is null)
        {
            return Failure(
                StatusCodes.Status409Conflict,
                localizer["The active publication version is unavailable."]);
        }

        var columns = version.Columns
            .Where(column => column.IsReadable)
            .OrderBy(column => column.Ordinal)
            .ToArray();
        if (columns.Length == 0)
        {
            return Failure(
                StatusCodes.Status409Conflict,
                localizer["The publication has no readable columns."]);
        }

        if (columns.Length > MaximumExcelColumns)
        {
            return Failure(
                StatusCodes.Status409Conflict,
                localizer[
                    "This table exposes {0} columns. Excel supports at most {1} columns per worksheet.",
                    columns.Length.ToString("N0", CultureInfo.CurrentCulture),
                    MaximumExcelColumns.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        var count = await sourceDataReader.CountRowsAsync(
                version,
                new PublicationSourceCountQuery([]),
                cancellationToken)
            .ConfigureAwait(false);
        if (count.Status != PublicationSourceReadStatus.Success || count.Value is null)
        {
            return SourceFailure(count.Status);
        }

        if (count.Value.TotalCount > MaximumExcelDataRows)
        {
            return Failure(
                StatusCodes.Status409Conflict,
                localizer[
                    "This table contains {0} rows. Excel supports at most {1} data rows plus one header row per worksheet.",
                    count.Value.TotalCount.ToString("N0", CultureInfo.CurrentCulture),
                    MaximumExcelDataRows.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            $"thub-publication-export-{Guid.NewGuid():N}.xlsx");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 64 * 1_024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.DeleteOnClose);
            await WriteWorkbookAsync(
                    stream,
                    publicationId,
                    roleIds,
                    version,
                    columns,
                    count.Value.TotalCount,
                    cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;
            return new(
                stream,
                $"{SafeFileName(publication.Name)}.xlsx",
                StatusCodes.Status200OK,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidOperationException or
            OverflowException or
            XmlException or
            PublicationXlsxLimitException or
            PublicationXlsxPolicyException)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            logger.LogWarning(
                exception,
                "Publication XLSX export failed for publication {PublicationId} and version {PublicationVersionId}.",
                publicationId,
                version.Id);
            return Failure(
                StatusCodes.Status409Conflict,
                exception is PublicationXlsxLimitException or PublicationXlsxPolicyException
                    ? exception.Message
                    : localizer["The XLSX file could not be generated."]);
        }
    }

    private async Task WriteWorkbookAsync(
        Stream destination,
        Guid publicationId,
        IReadOnlyCollection<Guid> roleIds,
        PublicationVersion version,
        IReadOnlyList<PublicationColumn> columns,
        long expectedRows,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Create(
            new NonClosingStream(destination),
            SpreadsheetDocumentType.Workbook,
            autoSave: true);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();

        using (var writer = OpenXmlWriter.Create(worksheetPart))
        {
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetViews());
            writer.WriteStartElement(new SheetView { WorkbookViewId = 0U });
            writer.WriteElement(new Pane
            {
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen
            });
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement(new SheetData());
            WriteRow(writer, columns.Select(column => (object?)column.PublicAlias));

            string? cursor = null;
            long writtenRows = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureStillAuthorizedAsync(
                        publicationId,
                        roleIds,
                        version.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
                var pageSize = Math.Min(ExportBatchSize, version.Settings.EditorWindowSize);
                var page = await sourceDataReader.ReadRowsAsync(
                        version,
                        new PublicationSourceReadQuery(
                            pageSize,
                            cursor,
                            [],
                            []),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (page.Status != PublicationSourceReadStatus.Success || page.Value is null)
                {
                    throw new InvalidOperationException(
                        $"The publication source became unavailable during export ({page.Status}).");
                }

                foreach (var row in page.Value.Rows)
                {
                    WriteRow(
                        writer,
                        columns.Select(column =>
                            row.TryGetValue(column.PublicAlias, out var value) ? value : null));
                    writtenRows++;
                }

                cursor = page.Value.NextCursor;
            }
            while (cursor is not null);

            if (writtenRows != expectedRows)
            {
                throw new InvalidOperationException(
                    "The table changed while it was being exported. Retry to produce a consistent file.");
            }

            writer.WriteEndElement();
            writer.WriteElement(new AutoFilter
            {
                Reference = $"A1:{ColumnName(columns.Count)}{writtenRows + 1}"
            });
            writer.WriteEndElement();
        }

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1U,
            Name = SafeSheetName(version.SourceObject)
        });
        workbookPart.Workbook.Save();
    }

    private async Task EnsureStillAuthorizedAsync(
        Guid publicationId,
        IReadOnlyCollection<Guid> roleIds,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
                publicationId,
                roleIds,
                PublicationOperation.View,
                cancellationToken)
            .ConfigureAwait(false);
        var publication = await catalogStore.FindAsync(publicationId, cancellationToken)
            .ConfigureAwait(false);
        if (!authorization.IsSuccess ||
            publication is null ||
            publication.Kind != PublicationKind.Editor ||
            publication.State != PublicationState.Active ||
            publication.ActiveVersionId != versionId)
        {
            throw new PublicationXlsxPolicyException(
                localizer[
                    "Your access or the active publication changed during export. Retry after refreshing the page."]);
        }
    }

    private void WriteRow(OpenXmlWriter writer, IEnumerable<object?> values)
    {
        writer.WriteStartElement(new Row());
        foreach (var value in values)
        {
            writer.WriteElement(CreateCell(value));
        }

        writer.WriteEndElement();
    }

    private Cell CreateCell(object? value)
    {
        if (value is null or DBNull)
        {
            return new Cell();
        }

        if (value is bool boolean)
        {
            return new Cell
            {
                DataType = CellValues.Boolean,
                CellValue = new CellValue(boolean ? "1" : "0")
            };
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
        {
            return NumberCell(Convert.ToString(value, CultureInfo.InvariantCulture)!);
        }

        if (value is float single && float.IsFinite(single))
        {
            return NumberCell(single.ToString("R", CultureInfo.InvariantCulture));
        }

        if (value is double number && double.IsFinite(number))
        {
            return NumberCell(number.ToString("R", CultureInfo.InvariantCulture));
        }

        var text = value switch
        {
            byte[] binary => Convert.ToBase64String(binary),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
        text = SanitizeXml(text);
        if (text.Length > MaximumExcelCellCharacters)
        {
            throw new PublicationXlsxLimitException(
                localizer[
                    "A cell exceeds Excel's {0}-character limit.",
                    MaximumExcelCellCharacters.ToString("N0", CultureInfo.CurrentCulture)]);
        }

        return new Cell
        {
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve })
        };
    }

    private static Cell NumberCell(string value) => new()
    {
        DataType = CellValues.Number,
        CellValue = new CellValue(value)
    };

    private static string SanitizeXml(string value) =>
        string.Concat(value.Where(XmlConvert.IsXmlChar));

    private static string ColumnName(int columnCount)
    {
        var value = columnCount;
        var name = string.Empty;
        while (value > 0)
        {
            value--;
            name = (char)('A' + value % 26) + name;
            value /= 26;
        }

        return name;
    }

    private static string SafeSheetName(string value)
    {
        var sanitized = new string(value
            .Where(character => character is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\'))
            .Take(31)
            .ToArray())
            .Trim('\'');
        return string.IsNullOrWhiteSpace(sanitized) ? "Data" : sanitized;
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Where(character => !invalid.Contains(character) && !char.IsControl(character))
            .Take(120)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "publication-data" : sanitized.Trim();
    }

    private PublicationXlsxExportResult SourceFailure(PublicationSourceReadStatus status) =>
        status switch
        {
            PublicationSourceReadStatus.SchemaChanged => Failure(
                StatusCodes.Status409Conflict,
                localizer["The source schema changed after this publication version was activated."]),
            PublicationSourceReadStatus.InvalidCursor => Failure(
                StatusCodes.Status409Conflict,
                localizer["The export cursor is invalid. Retry the export."]),
            _ => Failure(
                StatusCodes.Status503ServiceUnavailable,
                localizer["The publication source is temporarily unavailable."])
        };

    private static PublicationXlsxExportResult Failure(int statusCode, string error) =>
        new(null, null, statusCode, error);

    private sealed class PublicationXlsxLimitException(string message) : Exception(message);

    private sealed class PublicationXlsxPolicyException(string message) : Exception(message);

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Flush();
            }
        }
    }
}
