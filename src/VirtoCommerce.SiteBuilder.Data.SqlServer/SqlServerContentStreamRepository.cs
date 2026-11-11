using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using VirtoCommerce.SiteBuilder.Data.Repositories;

namespace VirtoCommerce.SiteBuilder.Data.SqlServer;

public class SqlServerContentStreamRepository(SiteBuilderDbContext dbContext)
    : ContentStreamRepository(dbContext)
{
    protected override string QuoteOpen => "[";
    protected override string QuoteClose => "]";
    protected override string EmptyContentLiteral => "N''";

    // SQL Server streams the append via the nvarchar(max) .WRITE() mutator (append at end, replace 0 chars).
    protected override string AppendContentChunkSql =>
        $"UPDATE {Table} SET {ContentColumn}.WRITE(@chunk, NULL, 0) WHERE {IdColumn} = @id";

    protected override void SetIdParameter(DbCommand cmd, string value)
    {
        cmd.Parameters.Add(new SqlParameter("@id", value));
    }

    protected override void SetContentChunk(DbCommand cmd, string chunk)
    {
        cmd.Parameters.Add(new SqlParameter("@chunk", SqlDbType.NVarChar, -1) { Value = chunk });
    }
}
