using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using THub.Domain.Publications;

namespace THub.Infrastructure.Persistence.Configurations;

public sealed class PublicationColumnConfiguration : IEntityTypeConfiguration<PublicationColumn>
{
    public void Configure(EntityTypeBuilder<PublicationColumn> builder)
    {
        builder.ToTable("PublicationColumns");
        builder.HasKey(column => column.Id);

        builder.Property(column => column.SourceName)
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(column => column.PublicAlias)
            .HasMaxLength(PublicationColumn.MaximumAliasLength)
            .IsRequired();
        builder.Property(column => column.DataType)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(column => column.SourceTypeName)
            .HasMaxLength(128)
            .IsRequired();

        builder.OwnsOne(column => column.ForeignKey, foreignKey =>
        {
            foreignKey.ToTable("PublicationColumnForeignKeys");
            foreignKey.Property(value => value.ConstraintName)
                .HasColumnName("ForeignKeyConstraintName")
                .HasMaxLength(128);
            foreignKey.Property(value => value.Ordinal)
                .HasColumnName("ForeignKeyOrdinal");
            foreignKey.Property(value => value.ColumnCount)
                .HasColumnName("ForeignKeyColumnCount");
            foreignKey.Property(value => value.ReferencedSchema)
                .HasColumnName("ForeignKeyReferencedSchema")
                .HasMaxLength(128);
            foreignKey.Property(value => value.ReferencedObject)
                .HasColumnName("ForeignKeyReferencedObject")
                .HasMaxLength(128);
            foreignKey.Property(value => value.ReferencedColumn)
                .HasColumnName("ForeignKeyReferencedColumn")
                .HasMaxLength(128);
            foreignKey.Property(value => value.DisplayColumn)
                .HasColumnName("ForeignKeyDisplayColumn")
                .HasMaxLength(128);
            foreignKey.Property(value => value.LookupMode)
                .HasColumnName("ForeignKeyLookupMode")
                .HasConversion<string>()
                .HasMaxLength(32);

            foreignKey.Ignore(value => value.IsComposite);
            foreignKey.Ignore(value => value.StoresDisplayValue);
            foreignKey.Ignore(value => value.SearchColumns);

            var searchColumns = foreignKey.Property<List<string>>("_searchColumns")
                .HasColumnName("ForeignKeySearchColumnsJson")
                .HasColumnType("nvarchar(4000)")
                .HasConversion(CreateSearchColumnsConverter());
            searchColumns.Metadata.SetValueComparer(CreateSearchColumnsComparer());
        });

        builder.HasIndex(column => new { column.PublicationVersionId, column.Ordinal })
            .IsUnique();
        builder.HasIndex(column => new { column.PublicationVersionId, column.SourceName })
            .IsUnique();
        builder.HasIndex(column => new { column.PublicationVersionId, column.PublicAlias })
            .IsUnique();
        builder.HasIndex(column => new { column.PublicationVersionId, column.KeyOrdinal })
            .IsUnique()
            .HasFilter("[KeyOrdinal] IS NOT NULL");
    }

    private static ValueConverter<List<string>, string> CreateSearchColumnsConverter() =>
        new(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<List<string>>(value, (JsonSerializerOptions?)null) ?? new List<string>());

    private static ValueComparer<List<string>> CreateSearchColumnsComparer() =>
        new(
            (left, right) =>
                ReferenceEquals(left, right) ||
                (left != null && right != null && left.SequenceEqual(right, StringComparer.Ordinal)),
            value => value.Aggregate(
                0,
                (hash, item) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(item))),
            value => value.ToList());
}
