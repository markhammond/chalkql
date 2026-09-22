package chalk.planner.redact;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;
import org.checkerframework.checker.nullness.qual.Nullable;

/**
 * The owner's hashing policy, and nothing else (D262, {@code docs/design/37-redacted-sql.md} §2).
 *
 * <ul>
 *   <li>The <b>structural hash</b> is SHA-256 of the statement in canonical form with every literal
 *       replaced by its type marker. Stable across processes, machines and host builds.
 *   <li>The <b>seed</b> is HMAC-SHA256 of that hash under the host's salt.
 *   <li>A <b>literal's pseudonym</b> is HMAC-SHA256, under that seed, of its type and its canonical
 *       value, rendered as the first eight hex characters.
 * </ul>
 *
 * <p>A pseudonym is not anonymisation: a low-entropy value is brute-forceable by anyone who knows
 * the shape and holds the salt. The salt is the host's secret and is never logged.
 */
public final class Pseudonyms {
  /** The marker's shape. Eight hex characters is what §1 shows and what a log line can carry. */
  private static final int HEX_CHARACTERS = 8;

  private static final String HMAC = "HmacSHA256";

  private final byte[] seed;

  private Pseudonyms(byte[] seed) {
    this.seed = seed;
  }

  /** SHA-256 of a structural form. */
  public static byte[] structuralHash(String structuralForm) {
    try {
      return MessageDigest.getInstance("SHA-256")
          .digest(structuralForm.getBytes(StandardCharsets.UTF_8));
    } catch (NoSuchAlgorithmException e) {
      throw new IllegalStateException("SHA-256 is not available on this JVM", e);
    }
  }

  /** The seed for one statement: the structural hash, keyed by the salt. */
  public static Pseudonyms forStatement(byte[] structuralHash, RedactionPolicy policy) {
    return new Pseudonyms(mac(policy.salt(), structuralHash));
  }

  /**
   * The marker one literal becomes: {@code /*REDACTED-<eight hex>:<type>*}{@code /}.
   *
   * @param type the literal's SQL type name as Calcite names it
   * @param canonical the literal's own canonical unparse
   */
  public String marker(String type, String canonical) {
    return marker(type, canonical, null);
  }

  /**
   * The same, with the name of the context entry the literal is a value of beside the type: {@code
   * /*REDACTED-<eight hex>:<type> @ctx.<name>*}{@code /}. The label is a rendering and nothing
   * else — it is not in the pseudonym's input, so a labelled marker carries the very pseudonym an
   * unlabelled one does.
   *
   * @param label the name, or null for a literal that is no bound value
   */
  public String marker(String type, String canonical, @Nullable String label) {
    return "/*REDACTED-"
        + pseudonym(type, canonical)
        + ":"
        + type
        + (label == null ? "" : " " + label)
        + "*/";
  }

  /** The marker a structural form uses: the type alone, because the seed is derived from it. */
  public static String structuralMarker(String type) {
    return "/*REDACTED:" + type + "*/";
  }

  /** The eight hex characters {@link #marker} renders. Separate so a test can assert on it. */
  public String pseudonym(String type, String canonical) {
    byte[] typeBytes = type.getBytes(StandardCharsets.UTF_8);
    byte[] valueBytes = canonical.getBytes(StandardCharsets.UTF_8);
    // type || 0x00 || canonical value. The separator is what stops ("AB", "C") and ("A", "BC")
    // from hashing the same, which matters because the type is attacker-visible in the marker.
    byte[] message = new byte[typeBytes.length + 1 + valueBytes.length];
    System.arraycopy(typeBytes, 0, message, 0, typeBytes.length);
    message[typeBytes.length] = 0;
    System.arraycopy(valueBytes, 0, message, typeBytes.length + 1, valueBytes.length);

    byte[] digest = mac(seed, message);
    StringBuilder hex = new StringBuilder(HEX_CHARACTERS);
    for (int i = 0; i < HEX_CHARACTERS / 2; i++) {
      hex.append(Character.forDigit((digest[i] >> 4) & 0xF, 16));
      hex.append(Character.forDigit(digest[i] & 0xF, 16));
    }
    return hex.toString();
  }

  private static byte[] mac(byte[] key, byte[] message) {
    try {
      Mac mac = Mac.getInstance(HMAC);
      mac.init(new SecretKeySpec(key, HMAC));
      return mac.doFinal(message);
    } catch (java.security.GeneralSecurityException e) {
      throw new IllegalStateException("HmacSHA256 is not available on this JVM", e);
    }
  }
}
