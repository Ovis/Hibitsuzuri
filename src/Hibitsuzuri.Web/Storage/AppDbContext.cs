using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Storage;

/// <summary>アプリのSQLite接続とスキーマ移行を扱う。機能のモデルは実装段階で追加する</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
