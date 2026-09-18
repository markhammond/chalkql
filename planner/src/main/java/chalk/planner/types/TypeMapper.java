package chalk.planner.types;

import static java.util.Objects.requireNonNull;

import chalk.ir.v1.Column;
import chalk.ir.v1.Field;
import chalk.ir.v1.RowType;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.UnsupportedFeatureException;
import java.util.List;
import org.apache.calcite.avatica.util.TimeUnit;
import org.apache.calcite.rel.type.RelDataType;
import org.apache.calcite.rel.type.RelDataTypeFactory;
import org.apache.calcite.rel.type.RelDataTypeField;
import org.apache.calcite.sql.SqlIntervalQualifier;
import org.apache.calcite.sql.parser.SqlParserPos;
import org.apache.calcite.sql.type.SqlTypeName;

/**
 * IR {@link Type} ↔ Calcite {@link RelDataType}, per the table in docs/design/03-planner.md §3.3.
 * The mapping is total in the IR → Calcite direction and partial the other way: anything Calcite can
 * produce that the IR has no vocabulary for is {@link UnsupportedFeatureException}, never an
 * approximation.
 */
public final class TypeMapper {
  private final RelDataTypeFactory typeFactory;

  public TypeMapper(RelDataTypeFactory typeFactory) {
    this.typeFactory = typeFactory;
  }

  public RelDataTypeFactory typeFactory() {
    return typeFactory;
  }

  /** The Calcite type for an IR type, including nullability. */
  public RelDataType toCalcite(Type type) {
    RelDataType base =
        switch (type.getKind()) {
          case TYPE_KIND_BOOL -> typeFactory.createSqlType(SqlTypeName.BOOLEAN);
          case TYPE_KIND_I8 -> typeFactory.createSqlType(SqlTypeName.TINYINT);
          case TYPE_KIND_I16 -> typeFactory.createSqlType(SqlTypeName.SMALLINT);
          case TYPE_KIND_I32 -> typeFactory.createSqlType(SqlTypeName.INTEGER);
          case TYPE_KIND_I64 -> typeFactory.createSqlType(SqlTypeName.BIGINT);
          case TYPE_KIND_FP32 -> typeFactory.createSqlType(SqlTypeName.REAL);
          case TYPE_KIND_FP64 -> typeFactory.createSqlType(SqlTypeName.DOUBLE);
          case TYPE_KIND_STRING -> typeFactory.createSqlType(SqlTypeName.VARCHAR);
          case TYPE_KIND_BINARY -> typeFactory.createSqlType(SqlTypeName.VARBINARY);
          case TYPE_KIND_DATE -> typeFactory.createSqlType(SqlTypeName.DATE);
          case TYPE_KIND_TIME -> typeFactory.createSqlType(SqlTypeName.TIME, type.getPrecision());
          case TYPE_KIND_TIMESTAMP ->
              typeFactory.createSqlType(SqlTypeName.TIMESTAMP, type.getPrecision());
          case TYPE_KIND_TIMESTAMP_TZ ->
              typeFactory.createSqlType(
                  SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE, type.getPrecision());
          case TYPE_KIND_DECIMAL ->
              typeFactory.createSqlType(SqlTypeName.DECIMAL, type.getPrecision(), type.getScale());
          case TYPE_KIND_UUID -> typeFactory.createSqlType(SqlTypeName.UUID);
          case TYPE_KIND_INTERVAL_DAY ->
              typeFactory.createSqlIntervalType(
                  new SqlIntervalQualifier(TimeUnit.DAY, TimeUnit.SECOND, SqlParserPos.ZERO));
          case TYPE_KIND_INTERVAL_YEAR ->
              typeFactory.createSqlIntervalType(
                  new SqlIntervalQualifier(TimeUnit.YEAR, TimeUnit.MONTH, SqlParserPos.ZERO));
          // Depth 1 (D58): the element is a scalar, so this recursion is one level and no more.
          case TYPE_KIND_LIST -> {
            if (!type.hasElement()) {
              throw new UnsupportedFeatureException(
                  "LIST with no element type", "docs/design/02-ir.md §3 requires Type.element on a LIST.");
            }
            yield typeFactory.createArrayType(toCalcite(type.getElement()), -1);
          }
          case TYPE_KIND_UNSPECIFIED, UNRECOGNIZED ->
              throw new UnsupportedFeatureException(
                  "type kind " + type.getKind(),
                  "The catalog declares a type this planner does not know.");
        };
    return typeFactory.createTypeWithNullability(base, type.getNullable());
  }

  /** The IR type for a Calcite type. */
  public Type toIr(RelDataType type) {
    Type.Builder builder = Type.newBuilder().setNullable(type.isNullable());
    SqlTypeName name = type.getSqlTypeName();
    if (name == null) {
      throw new UnsupportedFeatureException(
          "Calcite type " + type, "It has no SQL type name, so the IR has no vocabulary for it.");
    }
    switch (name) {
      case BOOLEAN -> builder.setKind(TypeKind.TYPE_KIND_BOOL);
      case TINYINT -> builder.setKind(TypeKind.TYPE_KIND_I8);
      case SMALLINT -> builder.setKind(TypeKind.TYPE_KIND_I16);
      case INTEGER -> builder.setKind(TypeKind.TYPE_KIND_I32);
      case BIGINT -> builder.setKind(TypeKind.TYPE_KIND_I64);
      case REAL -> builder.setKind(TypeKind.TYPE_KIND_FP32);
      // Calcite's FLOAT is 8-byte; the IR has no separate kind for it (03-planner.md §3.3).
      case FLOAT, DOUBLE -> builder.setKind(TypeKind.TYPE_KIND_FP64);
      // CHAR(n) loses its padding semantics on the way out, which §3.3 accepts explicitly.
      case CHAR, VARCHAR -> builder.setKind(TypeKind.TYPE_KIND_STRING);
      case BINARY, VARBINARY -> builder.setKind(TypeKind.TYPE_KIND_BINARY);
      case DATE -> builder.setKind(TypeKind.TYPE_KIND_DATE);
      case TIME -> builder.setKind(TypeKind.TYPE_KIND_TIME).setPrecision(precisionOf(type));
      case TIMESTAMP ->
          builder.setKind(TypeKind.TYPE_KIND_TIMESTAMP).setPrecision(precisionOf(type));
      case TIMESTAMP_WITH_LOCAL_TIME_ZONE ->
          builder.setKind(TypeKind.TYPE_KIND_TIMESTAMP_TZ).setPrecision(precisionOf(type));
      case DECIMAL ->
          builder
              .setKind(TypeKind.TYPE_KIND_DECIMAL)
              .setPrecision(type.getPrecision())
              .setScale(Math.max(type.getScale(), 0));
      case UUID -> builder.setKind(TypeKind.TYPE_KIND_UUID);
      case INTERVAL_DAY,
              INTERVAL_DAY_HOUR,
              INTERVAL_DAY_MINUTE,
              INTERVAL_DAY_SECOND,
              INTERVAL_HOUR,
              INTERVAL_HOUR_MINUTE,
              INTERVAL_HOUR_SECOND,
              INTERVAL_MINUTE,
              INTERVAL_MINUTE_SECOND,
              INTERVAL_SECOND ->
          builder.setKind(TypeKind.TYPE_KIND_INTERVAL_DAY);
      case INTERVAL_YEAR, INTERVAL_YEAR_MONTH, INTERVAL_MONTH ->
          builder.setKind(TypeKind.TYPE_KIND_INTERVAL_YEAR);
      // Calcite's ARRAY and MULTISET both reach the IR as LIST; MULTISET has no order to lose,
      // because the only thing that produces one — COLLECT — is not a function the IR has (D58).
      case ARRAY -> {
        RelDataType element = requireNonNull(type.getComponentType(), "ARRAY without a component type");
        if (element.getSqlTypeName() == SqlTypeName.ARRAY
            || element.getSqlTypeName() == SqlTypeName.MULTISET
            || element.getSqlTypeName() == SqlTypeName.MAP
            || element.getSqlTypeName() == SqlTypeName.ROW) {
          throw new UnsupportedFeatureException(
              "a nested collection type (" + type + ")",
              "v1 lists are exactly one level deep and hold scalars "
                  + "(docs/design/14-windows-ii.md §5).");
        }
        builder.setKind(TypeKind.TYPE_KIND_LIST).setElement(toIr(element));
      }
      default ->
          throw new UnsupportedFeatureException(
              "SQL type " + name,
              "docs/design/02-ir.md §3 lists the types the IR carries; this is not one of them.");
    }
    return builder.build();
  }

  /** A Calcite row type from catalog columns, in declared order. */
  public RelDataType toCalciteRow(List<Column> columns) {
    RelDataTypeFactory.Builder builder = typeFactory.builder();
    for (Column column : columns) {
      builder.add(column.getName(), toCalcite(column.getType()));
    }
    return builder.build();
  }

  /** A Calcite row type from an IR row type, in declared order (a table function's RETURNS TABLE). */
  public RelDataType toCalciteRowType(RowType row) {
    RelDataTypeFactory.Builder builder = typeFactory.builder();
    for (Field field : row.getFieldsList()) {
      builder.add(field.getName(), toCalcite(field.getType()));
    }
    return builder.build();
  }

  /** An IR row type from a Calcite row type. Field names are carried verbatim, duplicates included. */
  public RowType toIrRow(RelDataType type) {
    RowType.Builder builder = RowType.newBuilder();
    for (RelDataTypeField field : type.getFieldList()) {
      builder.addFields(Field.newBuilder().setName(field.getName()).setType(toIr(field.getType())));
    }
    return builder.build();
  }

  /**
   * Calcite reports {@code PRECISION_NOT_SPECIFIED} for a temporal type written without one; the IR
   * always carries a number, and zero is the SQL default.
   */
  private static int precisionOf(RelDataType type) {
    int precision = type.getPrecision();
    return precision == RelDataType.PRECISION_NOT_SPECIFIED ? 0 : precision;
  }
}
