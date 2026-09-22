package chalk.planner.plan;

import chalk.ir.v1.Literal;
import chalk.ir.v1.Type;
import chalk.ir.v1.TypeKind;
import chalk.planner.rpc.v1.ParameterHint;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * A request's hints, checked against the types the statement inferred for its parameters (D284, the
 * wire; design 49 §2).
 *
 * <p>This runs <b>after</b> validation, because the inferred type is what a hint is checked against
 * and there is no inferred type until the statement has validated. It reads the wire messages and
 * produces the map {@link ParameterHints} holds, so the hint class itself is still read by the three
 * places the firewall names and by nothing else.
 *
 * <p>The check is by <em>family</em> rather than by exact type: a host thinking in CLR values sends
 * an {@code int} for a parameter the statement inferred as {@code DECIMAL(38,0)}, and refusing that
 * would make the API unusable. What is refused is a hint that cannot be about this parameter at all
 * — a string for a timestamp, a floating value for a count — and the refusal names the parameter,
 * its type and the hint's type, never the hint's value.
 */
public final class ParameterHintCheck {
  private ParameterHintCheck() {}

  /** A hint this statement's parameters cannot be about. {@code INVALID_REQUEST} to the host. */
  public static final class InvalidHintException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    InvalidHintException(String message) {
      super(message);
    }
  }

  /**
   * The hints as {@link ParameterHints} holds them, refusing any the statement cannot be about.
   *
   * @param hints the request's hints, in the order it sent them
   * @param parameterTypes the IR type of each of the statement's parameters, by ordinal
   */
  public static Map<Integer, ParameterHints.Hint> resolve(
      List<ParameterHint> hints, List<Type> parameterTypes) {
    Map<Integer, ParameterHints.Hint> resolved = new LinkedHashMap<>();
    for (ParameterHint hint : hints) {
      int ordinal = hint.getOrdinal();
      if (ordinal < 0 || ordinal >= parameterTypes.size()) {
        throw new InvalidHintException(
            "a parameter value hint names parameter "
                + ordinal
                + " and this statement has "
                + parameterTypes.size()
                + (parameterTypes.size() == 1 ? " parameter" : " parameters")
                + ". A hint is optional, but a hint for a parameter that is not there is a mistake "
                + "rather than nothing said.");
      }

      Type type = parameterTypes.get(ordinal);
      switch (hint.getValueCase()) {
        case LITERAL -> {
          Literal literal = hint.getLiteral();
          if (literal.getValueCase() == Literal.ValueCase.IS_NULL) {
            // A typed NULL written the long way round: the same statement as `is_null`.
            resolved.put(ordinal, new ParameterHints.Hint(null, type));
            break;
          }

          Family wanted = familyOf(type.getKind());
          Family given = familyOf(kindOf(literal));
          if (wanted != given) {
            throw new InvalidHintException(
                "the value hint for parameter "
                    + ordinal
                    + " is "
                    + given.text
                    + " and that parameter is "
                    + describe(type)
                    + ", which is "
                    + wanted.text
                    + ". A hint informs an estimate about the parameter it names, so it has to be a "
                    + "value that parameter could take.");
          }

          resolved.put(ordinal, new ParameterHints.Hint(literal, type));
        }
        case IS_NULL -> resolved.put(ordinal, new ParameterHints.Hint(null, type));
        // A hint that says nothing is not a hint, and not an error either: a sparse container is the
        // API's own shape. It is dropped, so only the ordinals the caller said something about are
        // hinted.
        case VALUE_NOT_SET -> resolved.remove(ordinal);
      }
    }

    return resolved;
  }

  /**
   * The families a hint and a parameter have to share. Coarse on purpose: within a family the hint
   * is coerced to the parameter's exact type at estimation time, as a literal bound is.
   */
  private enum Family {
    BOOLEAN("a boolean"),
    EXACT_NUMERIC("an exact number"),
    APPROXIMATE_NUMERIC("a floating-point number"),
    CHARACTER("a string"),
    BINARY("a binary string"),
    DATE("a date"),
    TIME("a time"),
    TIMESTAMP("a timestamp"),
    TIMESTAMP_TZ("a timestamp with a time zone"),
    INTERVAL_DAY("a day-time interval"),
    INTERVAL_YEAR("a year-month interval"),
    UUID("a UUID"),
    LIST("a list"),
    UNKNOWN("a value of no known kind");

    private final String text;

    Family(String text) {
      this.text = text;
    }
  }

  private static Family familyOf(TypeKind kind) {
    return switch (kind) {
      case TYPE_KIND_BOOL -> Family.BOOLEAN;
      case TYPE_KIND_I8, TYPE_KIND_I16, TYPE_KIND_I32, TYPE_KIND_I64, TYPE_KIND_DECIMAL ->
          Family.EXACT_NUMERIC;
      case TYPE_KIND_FP32, TYPE_KIND_FP64 -> Family.APPROXIMATE_NUMERIC;
      case TYPE_KIND_STRING -> Family.CHARACTER;
      case TYPE_KIND_BINARY -> Family.BINARY;
      case TYPE_KIND_DATE -> Family.DATE;
      case TYPE_KIND_TIME -> Family.TIME;
      case TYPE_KIND_TIMESTAMP -> Family.TIMESTAMP;
      case TYPE_KIND_TIMESTAMP_TZ -> Family.TIMESTAMP_TZ;
      case TYPE_KIND_INTERVAL_DAY -> Family.INTERVAL_DAY;
      case TYPE_KIND_INTERVAL_YEAR -> Family.INTERVAL_YEAR;
      case TYPE_KIND_UUID -> Family.UUID;
      case TYPE_KIND_LIST -> Family.LIST;
      default -> Family.UNKNOWN;
    };
  }

  /** The kind a hint's value case is, which is how a hint declares its own type. */
  private static TypeKind kindOf(Literal literal) {
    return switch (literal.getValueCase()) {
      case BOOL_VALUE -> TypeKind.TYPE_KIND_BOOL;
      case I8_VALUE -> TypeKind.TYPE_KIND_I8;
      case I16_VALUE -> TypeKind.TYPE_KIND_I16;
      case I32_VALUE -> TypeKind.TYPE_KIND_I32;
      case I64_VALUE -> TypeKind.TYPE_KIND_I64;
      case FP32_VALUE -> TypeKind.TYPE_KIND_FP32;
      case FP64_VALUE -> TypeKind.TYPE_KIND_FP64;
      case STRING_VALUE -> TypeKind.TYPE_KIND_STRING;
      case BINARY_VALUE -> TypeKind.TYPE_KIND_BINARY;
      case DATE_VALUE -> TypeKind.TYPE_KIND_DATE;
      case TIME_VALUE -> TypeKind.TYPE_KIND_TIME;
      case TIMESTAMP_VALUE -> TypeKind.TYPE_KIND_TIMESTAMP;
      case TIMESTAMP_TZ_VALUE -> TypeKind.TYPE_KIND_TIMESTAMP_TZ;
      case DECIMAL_VALUE -> TypeKind.TYPE_KIND_DECIMAL;
      case UUID_VALUE -> TypeKind.TYPE_KIND_UUID;
      case INTERVAL_DAY_VALUE -> TypeKind.TYPE_KIND_INTERVAL_DAY;
      case INTERVAL_YEAR_VALUE -> TypeKind.TYPE_KIND_INTERVAL_YEAR;
      case LIST_VALUE -> TypeKind.TYPE_KIND_LIST;
      default -> TypeKind.TYPE_KIND_UNSPECIFIED;
    };
  }

  /** The parameter's type as a reader of the refusal would write it: {@code DECIMAL(38,0)}. */
  private static String describe(@Nullable Type type) {
    if (type == null) {
      return "of no known type";
    }

    String name = type.getKind().name().replace("TYPE_KIND_", "");
    return switch (type.getKind()) {
      case TYPE_KIND_DECIMAL -> name + "(" + type.getPrecision() + "," + type.getScale() + ")";
      case TYPE_KIND_TIME, TYPE_KIND_TIMESTAMP, TYPE_KIND_TIMESTAMP_TZ ->
          name + "(" + type.getPrecision() + ")";
      default -> name;
    };
  }
}
