namespace Nancy.Session.Relational

/// The relational server SQL dialect to use
type Dialect =
  | SqlServer = 0
  | PostgreSql = 1
  | MySql = 2
  | SQLite = 3
