// Workflow Designer — D3 rendering module
//
// Phase 5 / Chunks 5-9: renders the workflow graph as SVG inside the
// canvas div, plus draws affordances on each node:
//   - + buttons on every non-terminal node's open slots (Chunks 5-7).
//   - X (delete) buttons in the top-left of every node except Start
//     (Chunk 9).
// Clicks propagate to Blazor via the DotNetObjectReference the page
// passes during mount(). Both affordances render only when dotNetRef
// is non-null (i.e. when the workflow is editable).
//
// Layout: subtree-width-aware, top-down from each root. Non-Decision
// parents sit directly above their single child; Decision parents
// branch left/right and edges route classic-flowchart-style (horizontal
// out of the left/right vertex, then vertical down to the child).
// Empty slots get a dashed edge terminating at a + button — the dashed
// edge is the "missing endpoint" cue. Non-empty slots get a + on the
// midpoint of the existing edge for "insert here".
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
const NODE_W = 110;
const NODE_H = 50;
const SUBTREE_GAP = 30;          // horizontal gap between Decision's two subtrees
const MIN_HALF_WIDTH = 80;       // minimum half-spacing for a Decision's branches
const TOP_PADDING = 40;
const LEFT_PADDING = 40;
const PLUS_RADIUS = 11;          // the + button circle's radius
const X_RADIUS = 9;              // the X (delete) button — slightly smaller

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

    // Wipe prior contents. Listeners attached to the container persist
    // across innerHTML clears, but the +-button model attaches listeners
    // only on the SVG elements (cleared with the SVG).
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
            kind: e.kind, // "path1" | "path2"
        }))
        .filter((e) => e.source && e.target);

    // Plus buttons: one per open slot on each non-terminal node. Build
    // before sizing the SVG because the buttons can extend the bottom
    // bound (empty slots draw the + at child-position-would-be).
    const plusButtons = entry.dotNetRef
        ? buildPlusButtons(positioned, positionedById)
        : [];

    // Bounds. Include + button positions so the canvas grows to fit
    // empty-slot +s that sit below the lowest node row, and to fit
    // wide Decision subtrees that push +s out to the right.
    const allXs = [
        ...positioned.map((n) => n.x + NODE_W / 2),
        ...plusButtons.map((b) => b.x + PLUS_RADIUS),
    ];
    const allYs = [
        ...positioned.map((n) => n.y + NODE_H / 2),
        ...plusButtons.map((b) => b.y + PLUS_RADIUS),
    ];
    const maxX = allXs.length ? Math.max(...allXs) : 0;
    const maxY = allYs.length ? Math.max(...allYs) : 0;
    const width = Math.max(600, maxX + LEFT_PADDING);
    const height = Math.max(360, maxY + TOP_PADDING);

    const svg = d3.select(container)
        .append("svg")
        .attr("width", width)
        .attr("height", height)
        .attr("viewBox", `0 0 ${width} ${height}`)
        .style("display", "block");

    // Real edges first so node shapes paint on top.
    svg.append("g")
        .attr("class", "edges")
        .selectAll("path")
        .data(drawableEdges)
        .enter()
        .append("path")
        .attr("d", (d) => edgePath(d.source, d.target, d.kind))
        .attr("fill", "none")
        .attr("stroke", "#9e9e9e")
        .attr("stroke-width", 1.5);

    // Dangling edges for empty slots. Same routing as real edges but
    // terminate at the + button instead of a child node, and rendered
    // dashed to read as "tentative / unresolved".
    const danglingButtons = plusButtons.filter((b) => b.slotEmpty);
    svg.append("g")
        .attr("class", "dangling-edges")
        .selectAll("path")
        .data(danglingButtons)
        .enter()
        .append("path")
        .attr("d", (d) => danglingEdgePath(positionedById.get(d.parentId), d))
        .attr("fill", "none")
        .attr("stroke", "#bdbdbd")
        .attr("stroke-width", 1.5)
        .attr("stroke-dasharray", "4 3");

    // Decision branch labels, rendered INSIDE the diamond near each
    // vertex. Same labels appear regardless of whether the slot is
    // filled or dangling — the label belongs to the slot, not the edge.
    const decisionNodes = positioned.filter(
        (n) => n.nodeTypeId === NODE_TYPE.Decision);
    const decisionLabels = [];
    for (const d of decisionNodes) {
        decisionLabels.push({ x: d.x - NODE_W / 2 + 10, y: d.y - 4, text: "1" });
        decisionLabels.push({ x: d.x + NODE_W / 2 - 10, y: d.y - 4, text: "2" });
    }
    svg.append("g")
        .attr("class", "decision-labels")
        .selectAll("text")
        .data(decisionLabels)
        .enter()
        .append("text")
        .attr("x", (d) => d.x)
        .attr("y", (d) => d.y)
        .attr("text-anchor", "middle")
        .attr("font-size", "9px")
        .attr("font-weight", "600")
        .attr("fill", "#5d4037")
        .text((d) => d.text);

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

    // + buttons, drawn last so they paint on top of nodes and edges.
    if (plusButtons.length > 0) {
        const plusG = svg.append("g").attr("class", "plus-buttons");
        plusG.selectAll("g")
            .data(plusButtons)
            .enter()
            .append("g")
            .attr("class", "plus-button")
            .attr("transform", (d) => `translate(${d.x}, ${d.y})`)
            .style("cursor", "pointer")
            .on("click", (event, d) => onPlusClick(event, d, entry.dotNetRef))
            .each(function () {
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

    // X (delete) buttons. One per node, except Start. Only rendered when
    // the workflow is editable (entry.dotNetRef is truthy).
    // Position: top-left corner of the node body. Diamonds get an inset
    // adjustment so the X doesn't float outside the visible fill area.
    if (entry.dotNetRef) {
        const deletableNodes = positioned.filter(
            (n) => n.nodeTypeId !== NODE_TYPE.Start);
        const xPositions = deletableNodes.map((n) => {
            // Diamond: inset further so the X sits over the diamond's
            // upper-left slope, not outside the shape. Use NODE_W/4
            // and NODE_H/4 for a comfortable position.
            const isDiamond = n.nodeTypeId === NODE_TYPE.Decision;
            const dx = isDiamond ? -NODE_W / 4 : -NODE_W / 2 + 12;
            const dy = isDiamond ? -NODE_H / 4 : -NODE_H / 2 + 12;
            return { nodeId: n.id, x: n.x + dx, y: n.y + dy };
        });

        const xG = svg.append("g").attr("class", "delete-buttons");
        xG.selectAll("g")
            .data(xPositions)
            .enter()
            .append("g")
            .attr("class", "delete-button")
            .attr("transform", (d) => `translate(${d.x}, ${d.y})`)
            .style("cursor", "pointer")
            .on("click", (event, d) => onDeleteClick(event, d.nodeId, entry.dotNetRef))
            .each(function () {
                const g = d3.select(this);
                g.append("circle")
                    .attr("r", X_RADIUS)
                    .attr("fill", "#fff")
                    .attr("stroke", "#c62828")  // Material red 800
                    .attr("stroke-width", 1.5);
                g.append("text")
                    .attr("text-anchor", "middle")
                    .attr("dominant-baseline", "central")
                    .attr("font-size", "13px")
                    .attr("font-weight", "600")
                    .attr("fill", "#c62828")
                    .style("pointer-events", "none")
                    .text("×");
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

// --- layout --------------------------------------------------------------

// Compute x/y pixel positions for every node. Algorithm:
//   1. Build a parent → child(ren) map from path1NodeId / path2NodeId.
//   2. Identify roots: nodes that aren't anyone's child. In a normal
//      Chunk 7 workflow this is exactly one Start; defensively we walk
//      every root we find (handles edge cases like orphaned subtrees
//      from older data).
//   3. For each root, walk top-down computing subtree widths bottom-up
//      first, then assigning x positions top-down using those widths.
//   4. y position is purely from executionLevel (the engine's truth).
//
// Subtree width: the horizontal span a node and its descendants occupy.
//   - Leaf or single-childless non-terminal: NODE_W.
//   - Non-Decision with one child: max(NODE_W, child's subtree width).
//   - Decision: leftWidth + rightWidth + SUBTREE_GAP, but at least
//     2 * MIN_HALF_WIDTH + SUBTREE_GAP so single-node branches still
//     get visible separation.
//
// When a Decision has no children yet (both slots empty), it still
// reserves space for its eventual branches via the MIN_HALF_WIDTH
// floor — so the two + buttons don't visually crowd the diamond.
function layout(nodes) {
    if (nodes.length === 0) return [];

    const nodesById = new Map(nodes.map((n) => [n.id, n]));
    const isChild = new Set();
    for (const n of nodes) {
        if (n.path1NodeId != null) isChild.add(n.path1NodeId);
        if (n.path2NodeId != null) isChild.add(n.path2NodeId);
    }
    const roots = nodes.filter((n) => !isChild.has(n.id));

    // Subtree-width memo. Key: node id. Value: pixel width the subtree
    // rooted at that node needs.
    const widths = new Map();
    function subtreeWidth(node) {
        if (widths.has(node.id)) return widths.get(node.id);
        let w;
        if (node.nodeTypeId === NODE_TYPE.Decision) {
            const left = node.path1NodeId != null
                ? subtreeWidth(nodesById.get(node.path1NodeId))
                : 2 * MIN_HALF_WIDTH;
            const right = node.path2NodeId != null
                ? subtreeWidth(nodesById.get(node.path2NodeId))
                : 2 * MIN_HALF_WIDTH;
            w = Math.max(NODE_W, left / 2 + right / 2 + SUBTREE_GAP + NODE_W);
            // The + NODE_W ensures the diamond itself fits even if both
            // subtrees are narrow.
        } else if (TERMINAL_TYPES.has(node.nodeTypeId)) {
            w = NODE_W;
        } else {
            // Start, Process: one child via path1 (path2 isn't valid).
            const child = node.path1NodeId != null
                ? nodesById.get(node.path1NodeId)
                : null;
            w = child ? Math.max(NODE_W, subtreeWidth(child)) : NODE_W;
        }
        widths.set(node.id, w);
        return w;
    }

    // Walk each root, assign positions. centerX = the x coordinate this
    // subtree should be centered on.
    const positioned = [];
    function place(node, centerX) {
        positioned.push({
            ...node,
            x: centerX,
            y: TOP_PADDING + (node.executionLevel - 1) * ROW_HEIGHT + NODE_H,
        });
        if (TERMINAL_TYPES.has(node.nodeTypeId)) return;

        if (node.nodeTypeId === NODE_TYPE.Decision) {
            // Each subtree gets its own half-width plus half the gap on
            // the inside. Subtree centers are placed exactly that
            // distance from the parent's centerX. This guarantees the
            // two subtrees don't overlap each other or the parent's
            // own diamond.
            const leftWidth = node.path1NodeId != null
                ? subtreeWidth(nodesById.get(node.path1NodeId))
                : 2 * MIN_HALF_WIDTH;
            const rightWidth = node.path2NodeId != null
                ? subtreeWidth(nodesById.get(node.path2NodeId))
                : 2 * MIN_HALF_WIDTH;
            const leftCenter = centerX - leftWidth / 2 - SUBTREE_GAP / 2;
            const rightCenter = centerX + rightWidth / 2 + SUBTREE_GAP / 2;
            if (node.path1NodeId != null) {
                place(nodesById.get(node.path1NodeId), leftCenter);
            }
            if (node.path2NodeId != null) {
                place(nodesById.get(node.path2NodeId), rightCenter);
            }
            // Empty slots produce no positioned child but their + button
            // position is computed later in buildPlusButtons using these
            // same conventions.
        } else if (node.path1NodeId != null) {
            // Non-Decision: single child directly below.
            place(nodesById.get(node.path1NodeId), centerX);
        }
    }

    // Lay out roots side by side. Most workflows have exactly one root
    // (the Start). Multiple roots happen with legacy orphan data. The
    // initial cursor sits at LEFT_PADDING; the post-pass shift below
    // adjusts if Decision asymmetry pushes the leftmost subtree below
    // the padding line.
    let cursorX = LEFT_PADDING;
    for (const r of roots) {
        const w = subtreeWidth(r);
        place(r, cursorX + w / 2);
        cursorX += w + SUBTREE_GAP * 2;
    }

    // Shift the whole graph so its leftmost extent is at LEFT_PADDING.
    // Asymmetric Decision subtrees can push a path1 child to a smaller
    // x than its parent's, occasionally below the initial cursorX. The
    // shift here keeps nothing clipped on the left edge of the viewBox.
    if (positioned.length > 0) {
        const minX = Math.min(...positioned.map((n) => n.x - NODE_W / 2));
        if (minX < LEFT_PADDING) {
            const shift = LEFT_PADDING - minX;
            for (const n of positioned) {
                n.x += shift;
            }
        }
    }

    return positioned;
}

// --- + buttons -----------------------------------------------------------

function buildPlusButtons(positioned, positionedById) {
    const buttons = [];
    for (const n of positioned) {
        if (TERMINAL_TYPES.has(n.nodeTypeId)) continue;
        buttons.push(makeButton(n, 1, positionedById));
        if (n.nodeTypeId === NODE_TYPE.Decision) {
            buttons.push(makeButton(n, 2, positionedById));
        }
    }
    return buttons;
}

function makeButton(parent, slot, positionedById) {
    const childId = slot === 1 ? parent.path1NodeId : parent.path2NodeId;
    const child = childId == null ? null : positionedById.get(childId);

    let x, y;
    if (child) {
        // Insert-between case. Position the + at the midpoint of the
        // existing edge so the click target sits "on the line" the user
        // is inserting into.
        if (parent.nodeTypeId === NODE_TYPE.Decision) {
            // The edge has an L-shape: horizontal then vertical.
            // Put the + at the corner of the L so it's visible against
            // either segment.
            x = child.x;
            y = (parent.y + child.y) / 2;
        } else {
            x = (parent.x + child.x) / 2;
            y = (parent.y + child.y) / 2;
        }
    } else {
        // Empty slot. Place the + where a future child's top would land,
        // so the dashed edge from the parent's slot anchor to the +
        // matches the shape a real edge would take once filled.
        y = parent.y + ROW_HEIGHT - PLUS_RADIUS - 4;
        if (parent.nodeTypeId === NODE_TYPE.Decision) {
            // Reserved width for an eventual child subtree.
            x = slot === 1
                ? parent.x - MIN_HALF_WIDTH - SUBTREE_GAP / 2
                : parent.x + MIN_HALF_WIDTH + SUBTREE_GAP / 2;
        } else {
            x = parent.x;
        }
    }

    return {
        parentId: parent.id,
        slot,
        slotEmpty: child == null,
        x,
        y,
    };
}

// --- edge routing --------------------------------------------------------

// Returns an SVG path "d" attribute for a real edge from parent to child.
//   - Non-Decision: vertical line, parent bottom-center → child top-center.
//   - Decision: L-shape. Leaves the left vertex going horizontally to
//     the child's x, then drops vertically to the child's top.
function edgePath(parent, child, kind) {
    if (parent.nodeTypeId !== NODE_TYPE.Decision) {
        const sx = parent.x;
        const sy = parent.y + NODE_H / 2;
        const tx = child.x;
        const ty = child.y - NODE_H / 2;
        return `M${sx},${sy} L${tx},${ty}`;
    }
    // Decision: leave the slot's vertex horizontally, then drop straight
    // down. The corner sits directly above the child.
    const vertexX = kind === "path1"
        ? parent.x - NODE_W / 2
        : parent.x + NODE_W / 2;
    const vertexY = parent.y;
    const turnX = child.x;
    const turnY = vertexY;
    const childTopX = child.x;
    const childTopY = child.y - NODE_H / 2;
    return `M${vertexX},${vertexY} L${turnX},${turnY} L${childTopX},${childTopY}`;
}

// Same shape as edgePath but terminates at the + button (slightly above
// the button's center so the line meets the circle's edge cleanly).
function danglingEdgePath(parent, plusButton) {
    if (!parent) return "";
    const stopX = plusButton.x;
    const stopY = plusButton.y - PLUS_RADIUS;
    if (parent.nodeTypeId !== NODE_TYPE.Decision) {
        const sx = parent.x;
        const sy = parent.y + NODE_H / 2;
        return `M${sx},${sy} L${stopX},${stopY}`;
    }
    const vertexX = plusButton.slot === 1
        ? parent.x - NODE_W / 2
        : parent.x + NODE_W / 2;
    const vertexY = parent.y;
    return `M${vertexX},${vertexY} L${stopX},${vertexY} L${stopX},${stopY}`;
}

// --- click handler -------------------------------------------------------

function onPlusClick(event, button, dotNetRef) {
    event.stopPropagation();
    if (!dotNetRef) return;

    dotNetRef.invokeMethodAsync(
        "OnPlusClickedAsync",
        button.parentId, button.slot, button.slotEmpty,
        Math.round(event.clientX), Math.round(event.clientY)
    ).catch((err) => {
        console.warn("workflow-designer: Blazor plus-click callback failed.", err);
    });
}

function onDeleteClick(event, nodeId, dotNetRef) {
    event.stopPropagation();
    if (!dotNetRef) return;

    dotNetRef.invokeMethodAsync("OnDeleteClickedAsync", nodeId)
        .catch((err) => {
            console.warn("workflow-designer: Blazor delete-click callback failed.", err);
        });
}

// --- shape drawing -------------------------------------------------------

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
