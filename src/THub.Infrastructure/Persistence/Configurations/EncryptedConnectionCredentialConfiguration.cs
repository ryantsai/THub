using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using THub.Infrastructure.Connections;

namespace THub.Infrastructure.Persistence.Configurations;

internal sealed class EncryptedConnectionCredentialConfiguration
    : IEntityTypeConfiguration<EncryptedConnectionCredential>
{
    public void Configure(
        EntityTypeBuilder<EncryptedConnectionCredential> entity)
    {
        entity.ToTable("EncryptedConnectionCredentials");
        entity.HasKey(credential => credential.SecretReference);
        entity.Property(credential => credential.SecretReference)
            .HasMaxLength(200);
        entity.Property(credential => credential.Nonce)
            .HasColumnType("binary(12)");
        entity.Property(credential => credential.Ciphertext)
            .HasMaxLength(ConnectionCredentialProtector.MaximumCiphertextBytes);
        entity.Property(credential => credential.AuthenticationTag)
            .HasColumnType("binary(16)");
        entity.HasIndex(credential => credential.UpdatedAtUtc);
    }
}
