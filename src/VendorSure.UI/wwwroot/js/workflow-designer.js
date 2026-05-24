// Workflow Designer — D3 rendering module
//
// Phase 5 / Chunks 5-7: renders the workflow graph as SVG inside the
// canvas div, plus draws + affordances on each non-terminal node's
// open slots. Clicks on + propagate to Blazor via the DotNetObjectReference
// the page passes during mount(). No drag/drop — Chunk 6's palette
// surface was superseded by the +-button model in Chunk 7.
//
// D3 is loaded as UMD via <script> in App.razor, so window.d3 is the
// entry point. This module is an ES module that holds onto small per-
// canvas state across re-mounts.

const state = new Map();

const NODE_TYPE = {
    Start: 1,
    Process: 2,
    Decision: 3,
    Approved: 4,
    Rejected: 5,
    Cancelled: 6,
};

const TERMINAL_TYPES = new Set([
    NODE_TYPE.Approved, NODE_TYPE.Rejected, NODE_TYPE.Cancelled,
]);

// Geometry — small workflows fit on a typical laptop screen without
// scrolling. Designer doesn't have zoom/pan yet; if workflows ever grow
// past ~12 nodes vertically we'll add it.
const ROW_HEIGHT = 110;          // vertical distance between levels
const COL_WIDTH = 150;
const TOP_PADDING = 40;
const LEFT_PADDING = 40;
const NODE_W = 110;
const NODE_H = 50;
const PLUS_RADIUS = 11;          // the + button circle's radius

// Per-node-type appearance. Fill/stroke colors are readable on both
// light and dark MudBlazor themes. Decision uses a saturated orange so
// the diamond shape is visually distinct from rectangles even at small
// sizes.
const NODE_STYLE = {
    [NODE_TYPE.Start]:     { shape: "oval",    fill: "#4caf50", stroke: "#2e7d32", text: "#fff" },
    [NODE_TYPE.Process]:   { shape: "rect",    fill: "#1976d2", stroke: "#0d47a1", text: "#fff" },
    [NODE_TYPE.Decision]:  { shape: "diamond", fill: "#ffa726", stroke: "#e65100", text: "#000" },
    [NODE_TYPE.Approved]:  { shape: "oval",    fill: "#43a047", stroke: "#1b5e20", text: "#fff" },
    [NODE_TYPE.Rejected]:  { shape: "oval",    fill: "#e53935", stroke: "#b71c1c", text: "#fff" },
    [NODE_TYPE.Cancelled]: { shape: "oval",    fill: "#757575", stroke: "#424242", text: "#fff" },
};

const NODE_LABEL = {
    [NODE_TYPE.Start]:     "Start",
    [NODE_TYPE.Process]:   "Process",
    [NODE_TYPE.Decision]:  "Decision",
    [NODE_TYPE.Approved]:  "Approved",
    [NODE_TYPE.Rejected]:  "Rejected",
    [NODE_TYPE.Cancelled]: "Cancelled",
};

export async function mount(selector, graphData, dotNetRef) {
    if (typeof window === "undefined" || !window.d3) {
        console.error("workflow-designer: window.d3 is not loaded. " +
            "Make sure lib/d3.v7.min.js is included in App.razor.");
        return;
    }
    const d3 = window.d3;

    const container = document.querySelector(selector);
    if (!container) {
        console.warn(`workflow-designer: container '${selector}' not found.`);
        return;
    }

    // Wipe prior contents (the SVG from the previous mount). Listeners
    // attached to the container itself persist across innerHTML clears,
    // but we don't attach any container-level listeners in the
    // +-button model — clicks go through delegated SVG handlers
    // attached during this mount() call, which get torn down with
    // the SVG.
    container.innerHTML = "";

    let entry = state.get(selector);
    if (!entry) {
        entry = {};
        state.set(selector, entry);
    }
    entry.dotNetRef = dotNetRef || entry.dotNetRef || null;

    const { nodes = [], edges = [] } = graphData || {};

    const positioned = layout(nodes);
    const positionedById = new Map(positioned.map((n) => [n.id, n]));
    const drawableEdges = edges
        .map((e) => ({
            source: positionedById.get(e.sourceId),
            target: positionedById.get(e.targetId),
            kind: e.kind,
        }))
        .filter((e) => e.source && e.target);

    // Size the SVG. Account for + buttons that sit below leaf nodes —
    // they extend the bottom bound by ~30px past NODE_H/2.
    const maxX = positioned.reduce((m, n) => Math.max(m, n.x), 0);
    const maxY = positioned.reduce((m, n) => Math.max(m, n.y), 0);
    const width = Math.max(600, maxX + NODE_W / 2 + LEFT_PADDING + 40);
    const height = Math.max(360, maxY + NODE_H / 2 + TOP_PADDING + 60);

    const svg = d3.select(container)
        .append("svg")
        .attr("width", width)
        .attr("height", height)
        .attr("viewBox", `0 0 ${width} ${height}`)
        .style("display", "block");

    // Edges first so node shapes paint on top.
    const linkGen = d3.linkVertical()
        .x((d) => d.x)
        .y((d) => d.y);

    svg.append("g")
        .attr("class", "edges")
        .selectAll("path")
        .data(drawableEdges)
        .enter()
        .append("path")
        .attr("d", (d) => linkGen({
            source: { x: d.source.x, y: d.source.y + NODE_H / 2 },
            target: { x: d.target.x, y: d.target.y - NODE_H / 2 },
        }))
        .attr("fill", "none")
        .attr("stroke", "#9e9e9e")
        .attr("stroke-width", 1.5);

    // Edge labels for Decision branches.
    svg.append("g")
        .attr("class", "edge-labels")
        .selectAll("text")
        .data(drawableEdges.filter((e) => e.source.nodeTypeId === NODE_TYPE.Decision))
        .enter()
        .append("text")
        .attr("x", (d) => (d.source.x + d.target.x) / 2 + 8)
        .attr("y", (d) => (d.source.y + d.target.y) / 2)
        .attr("text-anchor", "start")
        .attr("font-size", "10px")
        .attr("fill", "#616161")
        .text((d) => d.kind === "path1" ? "1" : "2");

    // Nodes.
    const nodeG = svg.append("g")
        .attr("class", "nodes")
        .selectAll("g")
        .data(positioned)
        .enter()
        .append("g")
        .attr("transform", (d) => `translate(${d.x}, ${d.y})`);

    nodeG.each(function (d) {
        const sel = d3.select(this);
        const style = NODE_STYLE[d.nodeTypeId] || NODE_STYLE[NODE_TYPE.Process];
        drawShape(sel, style);
        sel.append("text")
            .attr("text-anchor", "middle")
            .attr("dominant-baseline", "middle")
            .attr("font-size", "12px")
            .attr("font-weight", "500")
            .attr("fill", style.text)
            .text(nodeLabel(d));
        sel.append("text")
            .attr("x", NODE_W / 2 - 4)
            .attr("y", -NODE_H / 2 + 12)
            .attr("text-anchor", "end")
            .attr("font-size", "9px")
            .attr("fill", style.text)
            .attr("opacity", 0.7)
            .text(`#${d.id}`);
    });

    // + buttons. One per open slot for non-terminal nodes:
    //   - Start/Process: one + button.
    //   - Decision: two + buttons (slot 1 left, slot 2 right).
    //   - Terminals: none.
    // Position depends on whether the slot is empty:
    //   - empty: below the parent at the spot a future child would sit
    //     (centered for Start/Process, left/right for Decision).
    //   - non-empty: on the edge midway between parent and child, so
    //     "insert between" feels literal.
    // We only render + buttons when there's a Blazor callback wired up
    // — read-only views (non-Draft versions) just show the graph.
    if (entry.dotNetRef) {
        const plusButtons = buildPlusButtons(positioned, positionedById);
        const plusG = svg.append("g").attr("class", "plus-buttons");
        plusG.selectAll("g")
            .data(plusButtons)
            .enter()
            .append("g")
            .attr("class", "plus-button")
            .attr("transform", (d) => `translate(${d.x}, ${d.y})`)
            .style("cursor", "pointer")
            .on("click", (event, d) => onPlusClick(event, d, entry.dotNetRef))
            .each(function (d) {
                const g = d3.select(this);
                g.append("circle")
                    .attr("r", PLUS_RADIUS)
                    .attr("fill", "#fff")
                    .attr("stroke", "#1976d2")
                    .attr("stroke-width", 1.5);
                g.append("text")
                    .attr("text-anchor", "middle")
                    .attr("dominant-baseline", "central")
                    .attr("font-size", "16px")
                    .attr("font-weight", "600")
                    .attr("fill", "#1976d2")
                    .style("pointer-events", "none")  // clicks pass to <g>
                    .text("+");
            });
    }

    entry.svg = svg;
    entry.nodeCount = positioned.length;
}

export async function dispose(selector) {
    const s = state.get(selector);
    if (s && s.svg) {
        s.svg.remove();
    }
    state.delete(selector);
    const container = document.querySelector(selector);
    if (container) {
        container.innerHTML = "";
    }
}

// --- internals -----------------------------------------------------------

function layout(nodes) {
    if (nodes.length === 0) return [];

    // Group by execution_level, spread evenly within each level. The
    // designer-side invariant (Chunk 7) means orphans no longer occur in
    // normal use, but defensive: any node at level 0 (orphan) sits at the
    // top with the Start row.
    const byLevel = new Map();
    for (const n of nodes) {
        const lvl = n.executionLevel || 1;
        if (!byLevel.has(lvl)) byLevel.set(lvl, []);
        byLevel.get(lvl).push(n);
    }

    const levels = [...byLevel.keys()].sort((a, b) => a - b);
    const out = [];
    for (const lvl of levels) {
        const nodesAtLevel = byLevel.get(lvl);
        const count = nodesAtLevel.length;
        for (let i = 0; i < count; i++) {
            const n = nodesAtLevel[i];
            out.push({
                ...n,
                x: LEFT_PADDING + COL_WIDTH * (i - (count - 1) / 2) + 320,
                y: TOP_PADDING + (lvl - 1) * ROW_HEIGHT + NODE_H,
            });
        }
    }
    return out;
}

function buildPlusButtons(positioned, positionedById) {
    const buttons = [];
    for (const n of positioned) {
        if (TERMINAL_TYPES.has(n.nodeTypeId)) continue;

        // Slot 1 — every non-terminal has it.
        buttons.push(makeButton(n, 1, positionedById));

        // Slot 2 — only Decision has a second slot.
        if (n.nodeTypeId === NODE_TYPE.Decision) {
            buttons.push(makeButton(n, 2, positionedById));
        }
    }
    return buttons;
}

function makeButton(parent, slot, positionedById) {
    // Slot's current child id from the data we were handed. If null,
    // it's an empty slot; we draw the + below the parent. If non-null,
    // we draw the + on the edge between parent and child.
    const childId = slot === 1 ? parent.path1NodeId : parent.path2NodeId;
    const child = childId == null ? null : positionedById.get(childId);

    const isDecisionParent = parent.nodeTypeId === NODE_TYPE.Decision;
    const offsetX = isDecisionParent
        ? (slot === 1 ? -COL_WIDTH / 3 : COL_WIDTH / 3)
        : 0;

    let x, y;
    if (child) {
        // Midpoint of the edge.
        x = (parent.x + child.x) / 2;
        y = (parent.y + child.y) / 2;
    } else {
        // Below the parent at the would-be child position.
        x = parent.x + offsetX;
        y = parent.y + NODE_H / 2 + 30;
    }

    return {
        parentId: parent.id,
        slot,
        slotEmpty: child == null,
        x,
        y,
    };
}

function onPlusClick(event, button, dotNetRef) {
    // Stop the click from bubbling — there's nothing else to handle it
    // today, but it keeps future per-node click handlers from getting
    // confused.
    event.stopPropagation();
    if (!dotNetRef) return;

    // Pass click viewport coords too so Blazor can position the popover
    // near the + button. The page is responsible for translating these
    // to wherever the popover anchor lives.
    dotNetRef.invokeMethodAsync(
        "OnPlusClickedAsync",
        button.parentId, button.slot, button.slotEmpty,
        Math.round(event.clientX), Math.round(event.clientY)
    ).catch((err) => {
        console.warn("workflow-designer: Blazor plus-click callback failed.", err);
    });
}

function nodeLabel(node) {
    return NODE_LABEL[node.nodeTypeId] || `Type ${node.nodeTypeId}`;
}

function drawShape(sel, style) {
    const w = NODE_W;
    const h = NODE_H;
    switch (style.shape) {
        case "rect":
            sel.append("rect")
                .attr("x", -w / 2).attr("y", -h / 2)
                .attr("width", w).attr("height", h)
                .attr("rx", 6).attr("ry", 6)
                .attr("fill", style.fill).attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;
        case "oval":
            sel.append("rect")
                .attr("x", -w / 2).attr("y", -h / 2)
                .attr("width", w).attr("height", h)
                .attr("rx", h / 2).attr("ry", h / 2)
                .attr("fill", style.fill).attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;
        case "diamond":
            sel.append("polygon")
                .attr("points",
                    `0,${-h / 2} ${w / 2},0 0,${h / 2} ${-w / 2},0`)
                .attr("fill", style.fill).attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;
        default:
            sel.append("rect")
                .attr("x", -w / 2).attr("y", -h / 2)
                .attr("width", w).attr("height", h)
                .attr("fill", "#ccc").attr("stroke", "#999");
    }
}
