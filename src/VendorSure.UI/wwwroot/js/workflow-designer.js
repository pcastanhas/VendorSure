// Workflow Designer — D3 rendering module
//
// Phase 5 / Chunk 5: read-only render of the workflow graph inside the
// canvas div. No drag, no zoom, no click handlers. The module owns the
// canvas DOM completely; Blazor never re-renders inside it after this
// module's first mount call.
//
// D3 is loaded as UMD via <script> in App.razor, so window.d3 is the entry
// point. This module is an ES module that holds onto a small per-canvas
// state so that subsequent calls (eventually: update) can find their
// SVG and re-render in place.

const state = new Map();

const NODE_TYPE = {
    Start: 1,
    Process: 2,
    Decision: 3,
    Approved: 4,
    Rejected: 5,
    Cancelled: 6,
};

// Geometry — kept conservative so workflows up to ~12 nodes fit without
// scrolling on a typical laptop screen. Chunks 7-10 will introduce zoom
// and pan; that's why these numbers don't need to be perfect now.
const ROW_HEIGHT = 100;
const COL_WIDTH = 140;
const TOP_PADDING = 40;
const LEFT_PADDING = 40;
const NODE_W = 110;
const NODE_H = 50;

// Per-node-type appearance. Fill/stroke colors are picked to be readable
// on both light and dark MudBlazor themes. The "seeded color per workflow"
// concept from CONCEPT.md will eventually override Process/Decision fills;
// terminals stay green/red/grey regardless of seed.
const NODE_STYLE = {
    [NODE_TYPE.Start]:     { shape: "oval",      fill: "#4caf50", stroke: "#2e7d32", text: "#fff" },
    [NODE_TYPE.Process]:   { shape: "rect",      fill: "#1976d2", stroke: "#0d47a1", text: "#fff" },
    [NODE_TYPE.Decision]:  { shape: "diamond",   fill: "#ffa726", stroke: "#e65100", text: "#000" },
    [NODE_TYPE.Approved]:  { shape: "oval",      fill: "#43a047", stroke: "#1b5e20", text: "#fff" },
    [NODE_TYPE.Rejected]:  { shape: "oval",      fill: "#e53935", stroke: "#b71c1c", text: "#fff" },
    [NODE_TYPE.Cancelled]: { shape: "oval",      fill: "#757575", stroke: "#424242", text: "#fff" },
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

    // Wipe any prior contents — defensive. The canvas div was empty on
    // first mount per the page shell, but we may be re-mounting after a
    // node create / edit (Chunk 6+) where the same container is reused.
    // innerHTML = "" removes the SVG children but leaves the container's
    // own event listeners (dragover / drop) intact, so we don't re-attach.
    container.innerHTML = "";

    // Per-canvas state. If this is a re-mount we update the existing
    // entry; on first mount we wire up the drop-zone listeners. The
    // listeners themselves stay attached for the page's lifetime —
    // dispose() removes them.
    let entry = state.get(selector);
    if (!entry) {
        entry = { listenersAttached: false };
        state.set(selector, entry);
    }
    entry.dotNetRef = dotNetRef || entry.dotNetRef || null;

    if (!entry.listenersAttached && entry.dotNetRef) {
        attachDropListeners(container, selector);
        entry.listenersAttached = true;
    }

    const { nodes = [], edges = [] } = graphData || {};

    // Compute layout: each node gets x and y in pixels. Even-spread per
    // execution_level, parent-driven slot ordering within the level.
    const positioned = layout(nodes);

    // Build the edge list with absolute coordinates so D3 doesn't need
    // to look up nodes during render.
    const positionedById = new Map(positioned.map((n) => [n.id, n]));
    const drawableEdges = edges
        .map((e) => ({
            source: positionedById.get(e.sourceId),
            target: positionedById.get(e.targetId),
            kind: e.kind, // "path1" | "path2"
        }))
        .filter((e) => e.source && e.target);

    // Size the SVG so all nodes fit. Add some right/bottom padding so
    // terminal nodes don't get clipped by the dashed border.
    const maxX = positioned.reduce((m, n) => Math.max(m, n.x), 0);
    const maxY = positioned.reduce((m, n) => Math.max(m, n.y), 0);
    const width = Math.max(600, maxX + NODE_W / 2 + LEFT_PADDING);
    const height = Math.max(360, maxY + NODE_H / 2 + TOP_PADDING);

    const svg = d3.select(container)
        .append("svg")
        .attr("width", width)
        .attr("height", height)
        .attr("viewBox", `0 0 ${width} ${height}`)
        .style("display", "block");

    // Edges first so nodes paint on top of them.
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

    // Edge labels for Decision branches (Yes/No equivalent — show path1/path2
    // text near the source node). Helps visually verify wiring during dev.
    svg.append("g")
        .attr("class", "edge-labels")
        .selectAll("text")
        .data(drawableEdges.filter((e) => e.source.nodeTypeId === NODE_TYPE.Decision))
        .enter()
        .append("text")
        .attr("x", (d) => (d.source.x + d.target.x) / 2)
        .attr("y", (d) => (d.source.y + d.target.y) / 2)
        .attr("text-anchor", "middle")
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

        // Tiny id badge for dev — same idea as the temporary node-list
        // table on the page. Helps you cross-reference an SVG shape
        // with the readout below.
        sel.append("text")
            .attr("x", NODE_W / 2 - 4)
            .attr("y", -NODE_H / 2 + 12)
            .attr("text-anchor", "end")
            .attr("font-size", "9px")
            .attr("fill", style.text)
            .attr("opacity", 0.7)
            .text(`#${d.id}`);
    });

    // Update state with the latest SVG handle but preserve the listener
    // wiring. dispose() needs the listener handles to detach cleanly.
    entry.svg = svg;
    entry.nodeCount = positioned.length;
}

export async function dispose(selector) {
    const s = state.get(selector);
    if (s) {
        // Detach the drag/drop listeners we attached on first mount. We
        // can rely on the saved handles existing only when
        // listenersAttached was true; the helper checks defensively.
        const container = document.querySelector(selector);
        if (container && s.detach) {
            s.detach();
        }
        if (s.svg) {
            s.svg.remove();
        }
        state.delete(selector);
    }
    const container = document.querySelector(selector);
    if (container) {
        container.innerHTML = "";
    }
}

// Attach drag/drop listeners to the canvas container. Called exactly
// once per canvas (gated by entry.listenersAttached in mount). The
// listeners stay alive across re-mounts because innerHTML clearing
// removes children, not listeners on the container itself.
//
// The dataTransfer payload is a JSON-serialized
// { nodeTypeId: number, blockCatalogId: number | null } object, set by
// the palette buttons' dragstart handler in Razor markup. Decoding here
// keeps the JS side dumb about which palette items exist — they're
// described by the C# code that renders them.
function attachDropListeners(container, selector) {
    const onDragOver = (event) => {
        // preventDefault is required by the HTML5 spec to mark this
        // element as a valid drop target. Without it, drop never fires.
        event.preventDefault();
        event.dataTransfer.dropEffect = "copy";
        container.classList.add("workflow-canvas--drop-target");
    };

    const onDragLeave = (event) => {
        // dragleave fires for child elements too; only remove the
        // highlight when we actually leave the container's bounding box.
        if (event.target === container) {
            container.classList.remove("workflow-canvas--drop-target");
        }
    };

    const onDrop = async (event) => {
        event.preventDefault();
        container.classList.remove("workflow-canvas--drop-target");

        const entry = state.get(selector);
        if (!entry || !entry.dotNetRef) {
            console.warn("workflow-designer: drop arrived with no Blazor callback.");
            return;
        }

        const raw = event.dataTransfer.getData("application/json");
        if (!raw) {
            // Not a palette drag — maybe a stray drop from the desktop.
            // Ignore quietly.
            return;
        }

        let payload;
        try {
            payload = JSON.parse(raw);
        } catch (err) {
            console.warn("workflow-designer: drop payload was not valid JSON.", err);
            return;
        }

        const nodeTypeId = Number(payload.nodeTypeId);
        const blockCatalogId = payload.blockCatalogId == null
            ? null
            : Number(payload.blockCatalogId);

        if (!Number.isFinite(nodeTypeId)) {
            console.warn("workflow-designer: drop payload missing nodeTypeId.", payload);
            return;
        }

        try {
            await entry.dotNetRef.invokeMethodAsync(
                "OnPaletteDropAsync", nodeTypeId, blockCatalogId);
        } catch (err) {
            // The Blazor side logs its own errors; the catch here is just
            // to prevent the unhandled-promise warning in the browser.
            console.warn("workflow-designer: Blazor drop callback failed.", err);
        }
    };

    container.addEventListener("dragover", onDragOver);
    container.addEventListener("dragleave", onDragLeave);
    container.addEventListener("drop", onDrop);

    // Save a single detacher so dispose() can clean up without needing
    // to know the individual handler references.
    const entry = state.get(selector);
    entry.detach = () => {
        container.removeEventListener("dragover", onDragOver);
        container.removeEventListener("dragleave", onDragLeave);
        container.removeEventListener("drop", onDrop);
        container.classList.remove("workflow-canvas--drop-target");
    };
}

// --- internals ------------------------------------------------------------

function layout(nodes) {
    if (nodes.length === 0) return [];

    // Group by execution_level so each level can be spread evenly.
    const byLevel = new Map();
    for (const n of nodes) {
        const lvl = n.executionLevel;
        if (!byLevel.has(lvl)) byLevel.set(lvl, []);
        byLevel.get(lvl).push(n);
    }

    // Within each level, sort by parent-driven slot if possible.
    // The repo's ListByWorkflowIdAsync already returns nodes sorted
    // by (level ASC, id ASC), so insertion order is stable; for
    // Decisions, path1 comes before path2 by id-of-target order, which
    // is good-enough for now. Chunks 6-7 will tighten this when edges
    // need to look right post-edit.

    // Build a map from level → ordered list of nodes for that level.
    const levels = [...byLevel.keys()].sort((a, b) => a - b);
    const out = [];
    for (const lvl of levels) {
        const nodesAtLevel = byLevel.get(lvl);
        const count = nodesAtLevel.length;
        for (let i = 0; i < count; i++) {
            const n = nodesAtLevel[i];
            out.push({
                ...n,
                x: LEFT_PADDING + COL_WIDTH * (i - (count - 1) / 2) + 300,
                y: TOP_PADDING + lvl * ROW_HEIGHT + NODE_H,
            });
        }
    }
    return out;
}

function nodeLabel(node) {
    // For Process/Decision, eventually show block_catalog name. For now,
    // show the node-type label. The temporary node-list table on the page
    // shows the block id so devs can still see the full picture.
    return NODE_LABEL[node.nodeTypeId] || `Type ${node.nodeTypeId}`;
}

function drawShape(sel, style) {
    const w = NODE_W;
    const h = NODE_H;

    switch (style.shape) {
        case "rect":
            sel.append("rect")
                .attr("x", -w / 2)
                .attr("y", -h / 2)
                .attr("width", w)
                .attr("height", h)
                .attr("rx", 6)
                .attr("ry", 6)
                .attr("fill", style.fill)
                .attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;

        case "oval":
            sel.append("rect")
                .attr("x", -w / 2)
                .attr("y", -h / 2)
                .attr("width", w)
                .attr("height", h)
                .attr("rx", h / 2)
                .attr("ry", h / 2)
                .attr("fill", style.fill)
                .attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;

        case "diamond":
            sel.append("polygon")
                .attr("points",
                    `0,${-h / 2} ${w / 2},0 0,${h / 2} ${-w / 2},0`)
                .attr("fill", style.fill)
                .attr("stroke", style.stroke)
                .attr("stroke-width", 1.5);
            break;

        default:
            sel.append("rect")
                .attr("x", -w / 2)
                .attr("y", -h / 2)
                .attr("width", w)
                .attr("height", h)
                .attr("fill", "#ccc")
                .attr("stroke", "#999");
    }
}
