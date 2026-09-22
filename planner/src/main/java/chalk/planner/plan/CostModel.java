package chalk.planner.plan;

import chalk.ir.v1.CostProfile;

/**
 * Cost model v8 (D38, and §3 of the join, window, windows-II and user-function designs). Costs come from a {@link CostProfile} the client declares per schema and per
 * table, table over schema over these defaults, field by field; a zero field means "inherit".
 *
 * <p><b>What v8 changed</b> (D276, F112, {@code 46-row-goals.md} §2.2). No constant moved; two rels
 * started counting differently.
 *
 * <ul>
 *   <li>A leaf that carries a <b>row goal</b> — a scan or an index lookup under a limit that will
 *       stop pulling — estimates and costs the rows it will actually be pulled for:
 *       {@code rows' = min(rows, goal)} for a scan, {@code min(matched rows, goal)} for a lookup,
 *       and the cost formulas are the v6 and v7 ones over {@code rows'}. A goaled leaf is a
 *       different rel from the same leaf without one, so nothing else in the model changes: the
 *       ordinary bottom-up metadata does the rest.
 *   <li>{@code ChalkTopN} puts {@code input rows × log2(offset + fetch + 1)} in <b>both</b> slots
 *       instead of its output row count in the one Volcano compares (F112). Its heap pass reads the
 *       whole input and was invisible to the optimiser, exactly as {@code ChalkSort}'s was before
 *       ADR 0017.
 * </ul>
 *
 * <p>v7 was the clustered constant (D257) and is unchanged.
 *
 * <p>The numbers are per unit of work, and the units are deliberately commensurate: with a filter
 * above a scan costing two units per table row, a lookup wins below roughly 50 % selectivity and
 * loses above it, which leaves margin either side of rev 3's 90 % criterion. A host whose rows are
 * expensive to fetch by position raises {@link #lookupRowCost}, and the planner stops choosing
 * lookups sooner.
 *
 * <p><b>Only the first argument of {@code makeCost} is compared.</b> Calcite's {@code VolcanoCost}
 * orders costs by {@code rowCount} alone (its {@code isLt} reads that field and nothing else), so
 * every Chalk rel puts its work estimate there as well as in {@code cpu}. Splitting them would make
 * the cpu term decorative (ADR 0015).
 */
public final class CostModel {
  /** Reading one row by scan. */
  public static final double DEFAULT_SCAN_ROW_COST = 1.0;

  /** One index range seek: about log2 of a 100 000-row table. */
  public static final double DEFAULT_LOOKUP_SEEK_COST = 17.0;

  /** One row fetched by index — the random-access penalty. */
  public static final double DEFAULT_LOOKUP_ROW_COST = 4.0;

  /** One round trip to a remote source (M4/M5). */
  public static final double DEFAULT_REMOTE_CALL_COST = 1000.0;

  /** One row fetched from a remote source (M4/M5). */
  public static final double DEFAULT_REMOTE_ROW_COST = 2.0;

  private final CostProfile profile;

  private CostModel(CostProfile profile) {
    this.profile = profile;
  }

  /** The model for a table whose resolved profile is {@code profile}. */
  public static CostModel of(CostProfile profile) {
    return new CostModel(profile == null ? CostProfile.getDefaultInstance() : profile);
  }

  /** The model nothing overrides. */
  public static CostModel defaults() {
    return new CostModel(CostProfile.getDefaultInstance());
  }

  public double scanRowCost() {
    return or(profile.getScanRowCost(), DEFAULT_SCAN_ROW_COST);
  }

  public double lookupSeekCost() {
    return or(profile.getLookupSeekCost(), DEFAULT_LOOKUP_SEEK_COST);
  }

  public double lookupRowCost() {
    return or(profile.getLookupRowCost(), DEFAULT_LOOKUP_ROW_COST);
  }

  public double remoteCallCost() {
    return or(profile.getRemoteCallCost(), DEFAULT_REMOTE_CALL_COST);
  }

  public double remoteRowCost() {
    return or(profile.getRemoteRowCost(), DEFAULT_REMOTE_ROW_COST);
  }

  /** Reading {@code rows} of a table, of whose columns {@code share} are projected. */
  public double scan(double rows, double share) {
    return rows * scanRowCost() * share;
  }

  /** {@code ranges} seeks and {@code rows} rows fetched by index. */
  public double lookup(int ranges, double rows) {
    return (ranges * lookupSeekCost()) + (rows * lookupRowCost());
  }

  /**
   * Cost model v7, the clustered constant (D257, {@code 34-clustered-indexes.md} §3): {@code ranges}
   * seeks and {@code rows} rows read <em>sequentially</em>, because a clustered index that covers the
   * lookup's projection holds those columns materialised in its own key order and the source serves
   * them as slices of that copy. The random-access penalty {@link #lookup} charges is exactly what
   * the copy removes, so the row term is the scan's.
   *
   * <p>Which means the planner chooses by projection, never by the index's name: the same index
   * prices a covered lookup as a scan and an uncovered one as a gather, and a full-range ordered scan
   * over a covering clustered index is priced as the sequential scan it is.
   */
  public double clusteredLookup(int ranges, double rows) {
    return (ranges * lookupSeekCost()) + (rows * scanRowCost());
  }

  /**
   * Cost model v3, join constants ({@code 12-joins.md} §3). Deliberately <b>not</b> per-source
   * profiles: a join is work the client does, not work a source does, so there is nobody to declare
   * a rate for it. The units are the same as everything else's — one unit is roughly one row
   * touched — which is what lets a join's cost be compared with a scan's beneath it.
   *
   * <p>The right input is the build side and the left is the probe side, by convention;
   * {@code JOIN_COMMUTE} offers the other orientation and these numbers decide.
   */
  public static double hashJoin(double probeRows, double buildRows, double outputRows) {
    return probeRows + (2 * buildRows) + outputRows;
  }

  /** One pass over each sorted input, plus the rows produced. */
  public static double mergeJoin(double leftRows, double rightRows, double outputRows) {
    return leftRows + rightRows + outputRows;
  }

  /**
   * Every left row against every right row. A <em>remote</em> inner side costs a round trip per
   * outer row on top of this, which {@code ChalkNestedLoopJoin} adds because only it can see whether
   * its inner side is a boundary (F51).
   */
  public static double nestedLoopJoin(double leftRows, double rightRows, double outputRows) {
    return (leftRows * rightRows) + outputRows;
  }

  /** Sort the build side once, then binary-search it per probe row. */
  public static double asOfJoin(double probeRows, double buildRows, double outputRows) {
    double log = log2(buildRows + 1);
    return (buildRows * log) + (probeRows * log) + outputRows;
  }

  /**
   * Cost model v4, the window constant ({@code 13-window-functions.md} §3): one pass over the input
   * per call, plus a log factor when the frame's two bounds both move and the operator needs a
   * monotonic deque or two pointers rather than a running accumulator.
   *
   * <p>The number is deliberately small. What cost decides for a window query is how its input
   * arrives — an index-ordered scan or a sort — and the window itself does the same work either way,
   * so a large constant here would only add noise to that comparison.
   */
  public static double window(double inputRows, int calls, boolean needsFrameSearch) {
    double work = inputRows * (1 + calls);
    return needsFrameSearch ? work + (inputRows * log2(inputRows + 1)) : work;
  }

  /**
   * Cost model v5 ({@code 14-windows-ii.md} §1 and §7). Three constants and three formulas.
   *
   * <p>Neither a hop's fan-out nor a list's length is something the catalog measures, so both are
   * stated here as an assumption rather than guessed per query. A hop's real fan-out is
   * {@code size / slide}, which the planner does know from the two interval literals — but the two
   * hop alternatives it would have to choose between differ in how their <em>input</em> arrives, not
   * in how many windows a row lands in, so a constant costs nothing and keeps the number stable.
   */
  public static final double HOP_WINDOWS_PER_ROW = 3.0;

  /** How many elements a list is assumed to hold, for want of a statistic that says. */
  public static final double UNNEST_ELEMENTS_PER_ROW = 4.0;

  /** One pass over the input, and one gather per output row. */
  public static double hop(double inputRows) {
    return inputRows * (1 + HOP_WINDOWS_PER_ROW);
  }

  /** Two passes over the input: one to number the sessions, one to write the bounds. */
  public static double session(double inputRows) {
    return 2 * inputRows;
  }

  /** One pass over the input, and one gather per element. */
  public static double unnest(double inputRows) {
    return inputRows * (1 + UNNEST_ELEMENTS_PER_ROW);
  }

  /**
   * Cost model v5, the set-operation constants ({@code 15-zero-allocation-execution.md} §6). A
   * {@code UNION ALL} is pure pass-through, so it costs the rows it forwards and nothing else; every
   * other form hashes, which reads the rows once to fill the table and once to probe or emit.
   */
  public static final double SET_OP_HASH_PASSES = 2.0;

  /** Σ input rows, doubled for the forms that hash. */
  public static double setOp(double rows, boolean passThrough) {
    return passThrough ? rows : rows * SET_OP_HASH_PASSES;
  }

  /** The same, as a Volcano cost over a Calcite {@code SetOp}'s inputs. */
  public static org.apache.calcite.plan.RelOptCost setOpCost(
      org.apache.calcite.rel.core.SetOp rel,
      org.apache.calcite.plan.RelOptPlanner planner,
      org.apache.calcite.rel.metadata.RelMetadataQuery mq,
      boolean passThrough) {
    double rows = 0;
    for (org.apache.calcite.rel.RelNode input : rel.getInputs()) {
      Double count = mq.getRowCount(input);
      rows += count == null ? 0 : count;
    }

    double work = setOp(rows, passThrough);
    return planner.getCostFactory().makeCost(work, work, 0);
  }

  /**
   * Cost model v6, the user-function constants (D78, {@code 17-user-defined-functions.md} §2). A
   * declared {@code COST} is per row and in these same units; the defaults say that a body the
   * planner inlined or a source evaluates costs about what a built-in does, and that leaving the
   * engine to call into the host costs four times as much — which is what decides whether a filter
   * over a client-bodied function is worth doing before or after a join.
   */
  public static final double DEFAULT_FUNCTION_COST = 1.0;

  /** A client body pays for the call, not only the arithmetic. */
  public static final double DEFAULT_CLIENT_FUNCTION_COST = 4.0;

  /** Rows a table function is assumed to produce when its declaration does not say. */
  public static final double DEFAULT_TABLE_FUNCTION_ROWS = 100.0;

  /** The per-row cost of one call to {@code declared}: its own {@code COST}, or the default. */
  public static double functionCost(chalk.planner.catalog.UserFunction declared) {
    double declaredCost = declared.descriptor().getCost();
    if (declaredCost != 0) {
      return declaredCost;
    }
    return declared.isClientBodied() ? DEFAULT_CLIENT_FUNCTION_COST : DEFAULT_FUNCTION_COST;
  }

  /** A table function's declared {@code ROWS}, or the default. */
  public static double tableFunctionRows(chalk.planner.catalog.UserFunction declared) {
    long rows = declared.descriptor().getRows();
    return rows > 0 ? rows : DEFAULT_TABLE_FUNCTION_ROWS;
  }

  /** Producing {@code rows} rows through a table function. */
  public static double tableFunctionCost(chalk.planner.catalog.UserFunction declared, double rows) {
    return rows * functionCost(declared);
  }

  /**
   * What the calls inside one expression add per row. Zero for an expression with none, which is
   * every expression in every plan before step 22.
   */
  public static double expressionFunctionCost(org.apache.calcite.rex.RexNode expression) {
    Adder adder = new Adder();
    expression.accept(adder);
    return adder.cost;
  }

  /** The same over a list of expressions — a projection's, or a window's calls. */
  public static double expressionFunctionCost(
      java.util.List<? extends org.apache.calcite.rex.RexNode> expressions) {
    double total = 0;
    for (org.apache.calcite.rex.RexNode expression : expressions) {
      total += expressionFunctionCost(expression);
    }
    return total;
  }

  private static final class Adder
      extends org.apache.calcite.rex.RexVisitorImpl<@org.checkerframework.checker.nullness.qual.Nullable Void> {
    private double cost;

    Adder() {
      super(true);
    }

    @Override
    public Void visitCall(org.apache.calcite.rex.RexCall call) {
      chalk.planner.catalog.UserFunction declared = UserOperators.declarationOf(call.getOperator());
      if (declared != null) {
        cost += functionCost(declared);
      }
      return super.visitCall(call);
    }
  }

  private static double log2(double value) {
    return Math.log(value) / Math.log(2);
  }

  /** A summary for {@code PlannerConfig.configHash}: the defaults, never a client's profile. */
  public static String summary() {
    return "scan="
        + DEFAULT_SCAN_ROW_COST
        + ",seek="
        + DEFAULT_LOOKUP_SEEK_COST
        + ",lookupRow="
        + DEFAULT_LOOKUP_ROW_COST
        // D257: a clustered lookup whose projection the copy covers pays the scan rate per row.
        + ",clusteredLookupRow="
        + DEFAULT_SCAN_ROW_COST
        + ",remoteCall="
        + DEFAULT_REMOTE_CALL_COST
        + ",remoteRow="
        + DEFAULT_REMOTE_ROW_COST
        + ",joins=hash(probe+2build+out),merge(l+r+out),nl(l*r+out[+l*remoteCall for an inner side"
        + " that fetches remotely anywhere]),asof((build+probe)log2 build+out)"
        + ",window=rows*(1+calls)[+rows*log2 rows when both frame bounds move]"
        + ",hop=rows*(1+" + HOP_WINDOWS_PER_ROW + "),session=2*rows,unnest=rows*(1+"
        + UNNEST_ELEMENTS_PER_ROW + "),setop=rows[*" + SET_OP_HASH_PASSES + " when hashing]"
        + ",function=" + DEFAULT_FUNCTION_COST + "[client " + DEFAULT_CLIENT_FUNCTION_COST
        + "],tableFunctionRows=" + DEFAULT_TABLE_FUNCTION_ROWS
        // M5 (D103, §2): what a cross-source strategy costs. A lookup pays per call and per matched
        // row; a broadcast pays one call plus the small side shipped; an adaptive join is costed as
        // the branch it will take, plus the materialisation neither LOCAL nor LOOKUP pays for.
        + ",lookupJoin=calls*remoteCall+matched*remoteRow+hash(driving,matched)"
        + ",broadcastJoin=remoteCall+keys*remoteRow+matched*remoteRow+hash(driving,matched)"
        + ",adaptiveJoin=min(lookup,local)+small"
        + ",partitionedScan=sum(partitions)"
        // v8 (D276, F112): a goaled leaf is costed for min(rows, goal), and the top-N's heap pass
        // is in the slot Volcano compares. Both change which plan is chosen, so both are in the
        // fingerprint a host compares two sidecars by.
        + ",rowGoal=min(rows,goal),topN=rows*log2(offset+fetch+1)";
  }

  private static double or(double declared, double fallback) {
    return declared != 0 ? declared : fallback;
  }
}
