using DotRocks.EntityFrameworkCore.Diagnostics;
using DotRocks.EntityFrameworkCore.Metadata.Conventions;
using DotRocks.EntityFrameworkCore.Migrations;
using DotRocks.EntityFrameworkCore.Query;
using DotRocks.EntityFrameworkCore.Storage;
using DotRocks.EntityFrameworkCore.Update;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;

namespace DotRocks.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Extension methods for registering DotRocks Entity Framework Core provider services.
/// </summary>
public static class DotRocksServiceCollectionExtensions
{
    /// <summary>
    /// Adds the DotRocks Entity Framework Core provider services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    public static IServiceCollection AddEntityFrameworkDotRocks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new EntityFrameworkRelationalServicesBuilder(services);
        builder.TryAdd<IProviderConventionSetBuilder, DotRocksConventionSetBuilder>();
        builder.TryAdd<IModelValidator, DotRocksModelValidator>();
        builder.TryAdd<IDatabaseProvider, DatabaseProvider<DotRocksOptionsExtension>>();
        builder.TryAdd<LoggingDefinitions, DotRocksLoggingDefinitions>();
        builder.TryAdd<IDatabase, DotRocksDatabase>();
        builder.TryAdd<IRelationalConnection, DotRocksRelationalConnection>();
        builder.TryAdd<IDatabaseCreator, DotRocksDatabaseCreator>();
        builder.TryAdd<IRelationalDatabaseCreator, DotRocksDatabaseCreator>();
        builder.TryAdd<ISqlGenerationHelper, DotRocksSqlGenerationHelper>();
        builder.TryAdd<IRelationalTypeMappingSource, DotRocksTypeMappingSource>();
        builder.TryAdd<IMethodCallTranslatorProvider, DotRocksMethodCallTranslatorProvider>();
        builder.TryAdd<
            IRelationalParameterBasedSqlProcessorFactory,
            DotRocksParameterBasedSqlProcessorFactory
        >();
        builder.TryAdd<IQuerySqlGeneratorFactory, DotRocksQuerySqlGeneratorFactory>();
        builder.TryAdd<IMigrationsSqlGenerator, DotRocksMigrationsSqlGenerator>();
        builder.TryAdd<IHistoryRepository, DotRocksHistoryRepository>();
        builder.TryAdd<IUpdateSqlGenerator, DotRocksUpdateSqlGenerator>();
        builder.TryAdd<IModificationCommandBatchFactory, DotRocksModificationCommandBatchFactory>();
        builder.TryAddCoreServices();
        return services;
    }
}
