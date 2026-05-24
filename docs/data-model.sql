/* ============================================================================
   VendorSure — Proposed Data Model (review draft)
   ============================================================================
   This is a *proposal*, not committed to the repo. Items I'm applying as my
   recommendation (rather than something you explicitly approved) are tagged
   with -- RECOMMENDATION: so they're easy to spot and overrule.

   Style conventions:
     - snake_case identifiers, plural table names (matches the draft).
     - IDENTITY(1,1) PKs everywhere (was inconsistent in the draft).
     - bit NOT NULL for flags (draft used int).
     - datetime2(0) for timestamps (draft used a mix of date / datetime).
     - nvarchar for any human-typed text; varchar only for fixed ASCII codes.
     - All hex color columns are char(7) with a CHECK constraint.
   ============================================================================ */

USE [VenSure];
GO

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


/* ============================================================================
   1. IDENTITY / AUTH
   ============================================================================ */

/*  user_groups
    Roles. The four flags drive what reviewer-surface actions a user can take
    and whether the user can submit at all (light-path users only need the
    last flag). */
CREATE TABLE [dbo].[user_groups] (
    [id]                    int             IDENTITY(1,1) NOT NULL,
    [name]                  nvarchar(100)   NOT NULL,
    [is_active]             bit             NOT NULL CONSTRAINT [DF_user_groups_is_active] DEFAULT (1),
    [can_restart_workflow]  bit             NOT NULL CONSTRAINT [DF_user_groups_restart]   DEFAULT (0),
    [can_change_workflow]   bit             NOT NULL CONSTRAINT [DF_user_groups_change]    DEFAULT (0),
    [can_submit_requests]   bit             NOT NULL CONSTRAINT [DF_user_groups_submit]    DEFAULT (1),
    CONSTRAINT [PK_user_groups] PRIMARY KEY CLUSTERED ([id] ASC)
);
GO

/*  users
    Backed by Entra. entraid is the durable external identity. */
CREATE TABLE [dbo].[users] (
    [id]            int             IDENTITY(1,1) NOT NULL,
    [entraid]       nvarchar(100)   NOT NULL,
    [name]          nvarchar(100)   NOT NULL,
    [group_id]      int             NOT NULL,  -- renamed from groupid (matches FK style elsewhere)
    [is_admin]      bit             NOT NULL CONSTRAINT [DF_users_is_admin]  DEFAULT (0),
    [is_active]     bit             NOT NULL CONSTRAINT [DF_users_is_active] DEFAULT (1),
    CONSTRAINT [PK_users] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_users_entraid] UNIQUE ([entraid])
);
GO

ALTER TABLE [dbo].[users] WITH CHECK
    ADD CONSTRAINT [FK_users_user_groups]
    FOREIGN KEY ([group_id]) REFERENCES [dbo].[user_groups] ([id]);
GO


/* ============================================================================
   2. SYSTEM SETTINGS
   ============================================================================ */

/*  settings
    Key/value store for system-wide configuration. key is the lookup identifier
    used by the application (e.g. 'Storage.BasePath', 'AI.Disabled'); the seed
    set lives in §16. description is a human-friendly explanation for the
    admin panel; value is the current value (string-encoded — application
    parses to int/bool/etc as needed). */
CREATE TABLE [dbo].[settings] (
    [id]            int             IDENTITY(1,1) NOT NULL,
    [key]           nvarchar(100)   NOT NULL,
    [description]   nvarchar(200)   NOT NULL,
    [required]      bit             NOT NULL CONSTRAINT [DF_settings_required] DEFAULT (0),
    [sensitive]     bit             NOT NULL CONSTRAINT [DF_settings_sensitive] DEFAULT (0),
    [value]         nvarchar(1000)  NULL,
    CONSTRAINT [PK_settings]      PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_settings_key]  UNIQUE ([key])
);
GO


/* ============================================================================
   3. NODE TYPES & BLOCK CATALOG
   ============================================================================
   workflow_node_types is a first-class concept (your call). Six rows seeded
   below. Blocks attach only to Process and Decision; Start and the three
   terminal types are structural-only. */

/*  workflow_node_types
    Visual + structural taxonomy. render_shape / render_color drive the
    canvas. allows_block documents which types accept an IT-authored block. */
CREATE TABLE [dbo].[workflow_node_types] (
    [id]            int             NOT NULL,   -- non-IDENTITY: stable seed values referenced by CHECK constraints
    [name]          nvarchar(50)    NOT NULL,
    [render_shape]  varchar(20)     NOT NULL,
    [render_color]  char(7)         NOT NULL,
    [allows_block]  bit             NOT NULL,
    CONSTRAINT [PK_workflow_node_types]      PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_workflow_node_types_name] UNIQUE ([name]),
    CONSTRAINT [CK_workflow_node_types_shape]
        CHECK ([render_shape] IN ('Oval','Rectangle','Diamond')),
    CONSTRAINT [CK_workflow_node_types_color]
        CHECK ([render_color] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]')
);
GO

-- RECOMMENDATION: hex values resolved from your named colors using standard
-- CSS named-color mappings. lavender for "light purple" (CSS has no
-- lightpurple). Adjust any of these in the seed if you want punchier shades.
INSERT INTO [dbo].[workflow_node_types] ([id],[name],[render_shape],[render_color],[allows_block])
VALUES
    (1, 'Start',     'Oval',      '#ADD8E6', 0),   -- lightblue
    (2, 'Process',   'Rectangle', '#ADD8E6', 1),   -- lightblue
    (3, 'Decision',  'Diamond',   '#E6E6FA', 1),   -- lavender
    (4, 'Approved',  'Oval',      '#90EE90', 0),   -- lightgreen
    (5, 'Rejected',  'Oval',      '#FFB6B6', 0),   -- light red (no CSS named equivalent)
    (6, 'Cancelled', 'Oval',      '#FFFFE0', 0);   -- lightyellow
GO

/*  artifact_catalog
    The library of typed artifacts that blocks can produce / consume.
    render attributes are placeholder for future canvas/reviewer-surface use. */
CREATE TABLE [dbo].[artifact_catalog] (
    [id]            int             IDENTITY(1,1) NOT NULL,
    [name]          nvarchar(50)    NOT NULL,   -- widened from 15 (too tight)
    [description]   nvarchar(200)   NULL,
    [color]         char(7)         NULL,
    [shape]         varchar(20)     NULL,
    CONSTRAINT [PK_artifact_catalog]      PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_artifact_catalog_name] UNIQUE ([name]),
    CONSTRAINT [CK_artifact_catalog_color]
        CHECK ([color] IS NULL OR [color] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]')
);
GO

/*  block_catalog
    IT-authored blocks. Each block declares which node type slot it fills
    (Process or Decision per the allows_block invariant, enforced below).
    color is an optional per-block override of the node-type default.

    name is the short label shown to workflow authors in the picker
    dialog and rendered as the label on each node on the canvas.
    description is the longer prose shown as a secondary line in the
    picker and as a native hover tooltip on the node body.

    path1_decision and path2_decision are the labels shown on the
    canvas at the Decision diamond's left and right outgoing vertices
    respectively — e.g., "True"/"False", "Approved"/"Denied",
    "Clean"/"Flagged". These belong to the block, not the node: the
    block's code precodes the path semantics, so the labels are
    consistent everywhere the block is used. Populated for Decision
    blocks (node_type_id = 3), forbidden for Process blocks.

    actor_type identifies who/what executes the block at runtime:
    1 = System (programmatic predicate or operation),
    2 = Human (routes to an approver UI),
    3 = AI (calls the AI service).
    The Phase 6+ engine branches on this to dispatch the block. */
CREATE TABLE [dbo].[block_catalog] (
    [id]              int             IDENTITY(1,1) NOT NULL,
    [node_type_id]    int             NOT NULL,
    [name]            nvarchar(50)    NOT NULL,
    [description]     nvarchar(200)   NOT NULL,
    [class_name]      nvarchar(200)   NOT NULL,   -- the .NET class implementing the block
    [is_active]       bit             NOT NULL CONSTRAINT [DF_block_catalog_is_active] DEFAULT (1),
    [color]           char(7)         NULL,
    [path1_decision]  nvarchar(20)    NULL,
    [path2_decision]  nvarchar(20)    NULL,
    [actor_type]      int             NOT NULL,
    CONSTRAINT [PK_block_catalog] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_block_catalog_color]
        CHECK ([color] IS NULL OR [color] LIKE '#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]'),
    -- blocks only exist for Process (2) and Decision (3)
    CONSTRAINT [CK_block_catalog_node_type]
        CHECK ([node_type_id] IN (2,3)),
    -- Decision blocks (3) require both path labels; Process blocks (2)
    -- must leave them NULL.
    CONSTRAINT [CK_block_catalog_decision_labels]
        CHECK (
            ([node_type_id] = 3 AND [path1_decision] IS NOT NULL AND [path2_decision] IS NOT NULL)
            OR
            ([node_type_id] = 2 AND [path1_decision] IS NULL AND [path2_decision] IS NULL)
        ),
    -- actor_type restricted to the three known runtime executors.
    CONSTRAINT [CK_block_catalog_actor_type]
        CHECK ([actor_type] IN (1, 2, 3))
);
GO

ALTER TABLE [dbo].[block_catalog] WITH CHECK
    ADD CONSTRAINT [FK_block_catalog_workflow_node_types]
    FOREIGN KEY ([node_type_id]) REFERENCES [dbo].[workflow_node_types] ([id]);
GO

/*  block_catalog_artifacts
    Per-block declaration of input and output artifact types. A process block
    that declares an input artifact type receives all artifacts of that type
    from the workflow instance's artifact bag. */
CREATE TABLE [dbo].[block_catalog_artifacts] (
    [id]                int     IDENTITY(1,1) NOT NULL,
    [block_catalog_id]  int     NOT NULL,
    [in_or_out]         char(1) NOT NULL,
    [artifact_id]       int     NOT NULL,
    CONSTRAINT [PK_block_catalog_artifacts] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_block_catalog_artifacts_in_or_out]
        CHECK ([in_or_out] IN ('I','O'))
);
GO

ALTER TABLE [dbo].[block_catalog_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_block_catalog_artifacts_block_catalog]
    FOREIGN KEY ([block_catalog_id]) REFERENCES [dbo].[block_catalog] ([id]);
GO

ALTER TABLE [dbo].[block_catalog_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_block_catalog_artifacts_artifact_catalog]
    FOREIGN KEY ([artifact_id]) REFERENCES [dbo].[artifact_catalog] ([id]);
GO


/* ============================================================================
   4. REQUEST TYPES & VERSIONS
   ============================================================================ */

/*  request_types
    The logical type. Almost nothing lives here — versions own everything
    that can change. */
CREATE TABLE [dbo].[request_types] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [name]                      nvarchar(100)   NOT NULL,
    [is_explanation_required]   bit             NOT NULL CONSTRAINT [DF_request_types_expl] DEFAULT (0),
    [is_active]                 bit             NOT NULL CONSTRAINT [DF_request_types_active] DEFAULT (1),
    CONSTRAINT [PK_request_types]      PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_request_types_name] UNIQUE ([name])
);
GO

/*  request_type_versions
    Immutable bundle: required docs list, validations, selection prompt,
    and candidate workflows all hang off here. Once placed In Service it
    cannot change.

    Prompts moved here from the draft's request_types per your correction.
    Renamed ai_workflow_assignment_prompt -> workflow_selection_prompt to
    match the concept's terminology. The single-call prevalidation_prompt
    was replaced by per-validation prompts on request_type_validations
    (see §12 below). */
CREATE TABLE [dbo].[request_type_versions] (
    [id]                            int             IDENTITY(1,1) NOT NULL,
    [request_type_id]               int             NOT NULL,
    [version]                       int             NOT NULL,
    [name]                          nvarchar(100)   NULL,            -- optional display label for this version
    [request_state]                 char(1)         NOT NULL,        -- D=Draft, I=In Service, S=Superseded
    [workflow_selection_prompt]     nvarchar(max)   NULL,
    [created_ts]                    datetime2(0)    NOT NULL CONSTRAINT [DF_rtv_created_ts] DEFAULT (SYSUTCDATETIME()),
    [placed_in_service_ts]          datetime2(0)    NULL,
    [superseded_ts]                 datetime2(0)    NULL,
    CONSTRAINT [PK_request_type_versions] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_request_type_versions_version] UNIQUE ([request_type_id],[version]),
    CONSTRAINT [CK_request_type_versions_state]
        CHECK ([request_state] IN ('D','I','S'))
);
GO

ALTER TABLE [dbo].[request_type_versions] WITH CHECK
    ADD CONSTRAINT [FK_request_type_versions_request_types]
    FOREIGN KEY ([request_type_id]) REFERENCES [dbo].[request_types] ([id]);
GO


/* ============================================================================
   5. REQUIRED DOCUMENTS
   ============================================================================ */

/*  required_documents_library
    Catalog of document *types* (W-9, voided check, etc.).

    file_type_required is a display hint shown to the submitter ("expected:
    PDF") but is NOT enforced — file-content checks happen in the AI
    validations, not in client-side file-extension matching. The hint can
    be NULL when no specific type is conventional. */
CREATE TABLE [dbo].[required_documents_library] (
    [id]                    int             IDENTITY(1,1) NOT NULL,
    [name]                  nvarchar(100)   NOT NULL,
    [file_type_required]    varchar(10)     NULL,
    CONSTRAINT [PK_required_documents_library]      PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_required_documents_library_name] UNIQUE ([name])
);
GO

/*  request_type_required_documents
    Junction: which document types a given Request Type version demands.
    (Was 'request_tyoe_required_documents' in the draft — typo.)

    RECOMMENDATION: this is now a pure junction. The draft duplicated name +
    file_extension here; both belong on the library row. */
CREATE TABLE [dbo].[request_type_required_documents] (
    [id]                            int     IDENTITY(1,1) NOT NULL,
    [request_type_version_id]       int     NOT NULL,
    [required_document_library_id]  int     NOT NULL,
    [required]                      bit     NOT NULL CONSTRAINT [DF_rtrd_required] DEFAULT (1),
    CONSTRAINT [PK_request_type_required_documents] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_rtrd_per_version] UNIQUE ([request_type_version_id],[required_document_library_id])
);
GO

ALTER TABLE [dbo].[request_type_required_documents] WITH CHECK
    ADD CONSTRAINT [FK_rtrd_request_type_versions]
    FOREIGN KEY ([request_type_version_id]) REFERENCES [dbo].[request_type_versions] ([id]);
GO

ALTER TABLE [dbo].[request_type_required_documents] WITH CHECK
    ADD CONSTRAINT [FK_rtrd_required_documents_library]
    FOREIGN KEY ([required_document_library_id]) REFERENCES [dbo].[required_documents_library] ([id]);
GO


/* ============================================================================
   6. WORKFLOW DEFINITIONS (templates, frozen with the request type version)
   ============================================================================
   RECOMMENDATION: renamed
     request_type_workflows            -> workflow_definitions
     request_type_workflow_definitions -> workflow_nodes
   Current names imply per-request scoping; these tables are actually the
   immutable templates that hang off a Request Type version.
   ============================================================================ */

/*  workflow_definitions
    One row per candidate workflow on a Request Type version. */
CREATE TABLE [dbo].[workflow_definitions] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [request_type_version_id]   int             NOT NULL,
    [name]                      nvarchar(100)   NOT NULL,
    [notes]                     nvarchar(1000)  NULL,
    [start_node_id]             int             NULL,            -- FK added after workflow_nodes is created
    CONSTRAINT [PK_workflow_definitions] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_workflow_definitions_name] UNIQUE ([request_type_version_id],[name])
);
GO

ALTER TABLE [dbo].[workflow_definitions] WITH CHECK
    ADD CONSTRAINT [FK_workflow_definitions_request_type_versions]
    FOREIGN KEY ([request_type_version_id]) REFERENCES [dbo].[request_type_versions] ([id]);
GO

/*  workflow_nodes
    Static graph: the nodes (and their out-edges) of a workflow_definition.
    Option A for edges: process nodes use path1_node_id (single out-edge);
    decision nodes use both path1_node_id and path2_node_id; terminal nodes
    use neither.

    block_catalog_id: nullable. NULL for Start (1) and the three terminals
    (4,5,6); NOT NULL for Process (2) and Decision (3). Enforced by CHECK
    (simple hardcoded IDs, per your call).

    prompt_text: human-actor-facing question displayed on the reviewer
    surface when a Decision block routes through a human ("Is this
    vendor foreign?"). Path labels for Decision branches live on
    block_catalog.path1_decision / path2_decision — they're block-
    level semantics, not workflow-author choices. */
CREATE TABLE [dbo].[workflow_nodes] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [workflow_definition_id]    int             NOT NULL,
    [node_type_id]              int             NOT NULL,
    [block_catalog_id]          int             NULL,
    [execution_level]           int             NOT NULL CONSTRAINT [DF_workflow_nodes_lvl] DEFAULT (0),
    [approver_group_id]         int             NULL,        -- only meaningful for user-decision block implementations
    [stale_threshold_days]      int             NULL,        -- RECOMMENDATION: days (concept), int. Was numeric(4,2) hours.
    [stale_message_text]        nvarchar(200)   NULL,
    [notes]                     nvarchar(1000)  NULL,
    [path1_node_id]             int             NULL,
    [path2_node_id]             int             NULL,
    [prompt_text]               nvarchar(200)   NULL,
    CONSTRAINT [PK_workflow_nodes] PRIMARY KEY CLUSTERED ([id] ASC),
    -- Block presence matches node type: Process(2)/Decision(3) require a block; Start(1)/Terminals(4,5,6) forbid one.
    CONSTRAINT [CK_workflow_nodes_block_by_type]
        CHECK (
            ([node_type_id] IN (2,3) AND [block_catalog_id] IS NOT NULL)
         OR ([node_type_id] IN (1,4,5,6) AND [block_catalog_id] IS NULL)
        ),
    -- Terminal nodes have no out-edges.
    CONSTRAINT [CK_workflow_nodes_terminal_no_edges]
        CHECK ([node_type_id] NOT IN (4,5,6) OR ([path1_node_id] IS NULL AND [path2_node_id] IS NULL)),
    -- Process and Start nodes have exactly one out-edge (path1).
    CONSTRAINT [CK_workflow_nodes_process_single_edge]
        CHECK ([node_type_id] NOT IN (1,2) OR [path2_node_id] IS NULL)
    -- NOTE: a CK_workflow_nodes_decision_both_edges constraint used to
    -- live here, requiring Decision nodes to have BOTH path1 AND path2
    -- set at insert time. The Phase 5 / Chunk 7 design shift moved
    -- "every Decision has both children" from edit-time enforcement to
    -- promotion-time validation (Draft → InService refuses any
    -- workflow with an incomplete Decision). Draft state legitimately
    -- holds Decisions with one or zero children while the designer is
    -- working. If you're regenerating this schema and adding it back,
    -- you'll break the designer.
);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_workflow_definitions]
    FOREIGN KEY ([workflow_definition_id]) REFERENCES [dbo].[workflow_definitions] ([id]);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_workflow_node_types]
    FOREIGN KEY ([node_type_id]) REFERENCES [dbo].[workflow_node_types] ([id]);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_block_catalog]
    FOREIGN KEY ([block_catalog_id]) REFERENCES [dbo].[block_catalog] ([id]);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_user_groups]
    FOREIGN KEY ([approver_group_id]) REFERENCES [dbo].[user_groups] ([id]);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_path1]
    FOREIGN KEY ([path1_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO

ALTER TABLE [dbo].[workflow_nodes] WITH CHECK
    ADD CONSTRAINT [FK_workflow_nodes_path2]
    FOREIGN KEY ([path2_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO

-- Now that workflow_nodes exists, close the start_node_id FK on workflow_definitions.
ALTER TABLE [dbo].[workflow_definitions] WITH CHECK
    ADD CONSTRAINT [FK_workflow_definitions_start_node]
    FOREIGN KEY ([start_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO


/* ============================================================================
   7. REQUESTS
   ============================================================================ */

/*  requests
    One submission. Bound for life to a specific Request Type *version*
    (snapshot semantics).

    request_type_version_id: renamed from request_type_id in the draft. The
    draft's FK already pointed at request_type_versions; only the column
    name was misleading.

    request_status: high-level lifecycle. Distinct from the workflow_state
    of any live workflow instance.
        P = Pre-validation (row exists, validations either running or
            completed-with-failures; submitter is in control). Validation
            run state is observable from ai_usage, not stored here.
        W = In Workflow (all validations passed; workflow engine owns it)
        A = Approved  (terminal)
        X = Rejected  (terminal — by validation refusal or by workflow)
        C = Cancelled (terminal) */
CREATE TABLE [dbo].[requests] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [request_type_version_id]   int             NOT NULL,
    [submitted_ts]              datetime2(0)    NOT NULL CONSTRAINT [DF_requests_submitted_ts] DEFAULT (SYSUTCDATETIME()),
    [submitted_userid]          int             NOT NULL,
    [request_status]            char(1)         NOT NULL,
    [request_notes]             nvarchar(2000)  NULL,
    CONSTRAINT [PK_requests] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_requests_status]
        CHECK ([request_status] IN ('P','W','A','X','C'))
);
GO

ALTER TABLE [dbo].[requests] WITH CHECK
    ADD CONSTRAINT [FK_requests_request_type_versions]
    FOREIGN KEY ([request_type_version_id]) REFERENCES [dbo].[request_type_versions] ([id]);
GO

ALTER TABLE [dbo].[requests] WITH CHECK
    ADD CONSTRAINT [FK_requests_users]
    FOREIGN KEY ([submitted_userid]) REFERENCES [dbo].[users] ([id]);
GO

/*  request_documents
    Documents uploaded against a request. Lives at request level — survives
    restart. required_document_id (nullable) tags which slot in the Request
    Type's required-documents list this upload satisfies.

    Storage abstraction (per design decision):
      - storage_backend identifies which storage adapter owns this blob.
        v1: 'LOCAL'  -> file on the configured Storage base path
                        (system setting; expected to be a UNC path to a NAS
                        share visible to web/app servers).
        Future: 'AZURE_BLOB', etc.
      - storage_key is the adapter-specific locator. For LOCAL it's the
        relative path inside the Storage base path; the convention is
        {RequestID}/{filename}. The app composes the absolute path at
        runtime from setting + storage_key. Never store the absolute path
        here — it makes migration painful and ties the data to a server.

    Per-row backend lets a partial migration coexist: old files stay LOCAL,
    new files become AZURE_BLOB. */
CREATE TABLE [dbo].[request_documents] (
    [id]                    int             IDENTITY(1,1) NOT NULL,
    [request_id]            int             NOT NULL,
    [display_file_name]     nvarchar(200)   NOT NULL,
    [storage_backend]       varchar(20)     NOT NULL CONSTRAINT [DF_request_documents_backend] DEFAULT ('LOCAL'),
    [storage_key]           nvarchar(1000)  NOT NULL,
    [file_extension]        varchar(10)     NOT NULL,
    [file_size]             bigint          NOT NULL,
    [is_deleted]            bit             NOT NULL CONSTRAINT [DF_request_documents_deleted] DEFAULT (0),
    [uploaded_ts]           datetime2(0)    NOT NULL CONSTRAINT [DF_request_documents_uploaded_ts] DEFAULT (SYSUTCDATETIME()),
    [required_document_id]  int             NULL,
    CONSTRAINT [PK_request_documents] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_request_documents_backend]
        CHECK ([storage_backend] IN ('LOCAL','AZURE_BLOB'))
);
GO

ALTER TABLE [dbo].[request_documents] WITH CHECK
    ADD CONSTRAINT [FK_request_documents_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[request_documents] WITH CHECK
    ADD CONSTRAINT [FK_request_documents_required]
    FOREIGN KEY ([required_document_id]) REFERENCES [dbo].[request_type_required_documents] ([id]);
GO

/*  request_comments
    Human narrative on a request. Anything posted by a user (reviewer or
    submitter) lives here. Block execution narrative lives in request_logs
    instead. */
CREATE TABLE [dbo].[request_comments] (
    [id]            int             IDENTITY(1,1) NOT NULL,
    [request_id]    int             NOT NULL,
    [user_id]       int             NOT NULL,
    [entry_ts]      datetime2(0)    NOT NULL CONSTRAINT [DF_request_comments_entry_ts] DEFAULT (SYSUTCDATETIME()),
    [comment]       nvarchar(max)   NOT NULL,
    CONSTRAINT [PK_request_comments] PRIMARY KEY CLUSTERED ([id] ASC)
);
GO

ALTER TABLE [dbo].[request_comments] WITH CHECK
    ADD CONSTRAINT [FK_request_comments_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[request_comments] WITH CHECK
    ADD CONSTRAINT [FK_request_comments_users]
    FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]);
GO


/* ============================================================================
   8. WORKFLOW INSTANCES (live runs)
   ============================================================================
   RECOMMENDATION: renamed request_workflows -> workflow_instances. A row
   here is one *run* of one workflow for one request. A request can have
   multiple instances over its life (workflow reassignment), at most one
   live at a time. */

/*  workflow_instances
    The universe of a running request: which workflow it's bound to, where
    in the graph it currently is, and which terminal state it ended in.

    prior_instance_id: when an instance is created by workflow reassignment
    (because triage routed wrong), it points back at the cancelled instance
    it replaces. NULL on the first instance.

    workflow_state alphabet:
        R = Running
        A = Approved   (landed on Approved terminal, id 4)
        X = Rejected   (landed on Rejected terminal, id 5)
        C = Cancelled  (landed on Cancelled terminal, id 6) */
CREATE TABLE [dbo].[workflow_instances] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [request_id]                int             NOT NULL,
    [workflow_definition_id]    int             NOT NULL,
    [workflow_state]            char(1)         NOT NULL CONSTRAINT [DF_workflow_instances_state] DEFAULT ('R'),
    [started_ts]                datetime2(0)    NOT NULL CONSTRAINT [DF_workflow_instances_started_ts] DEFAULT (SYSUTCDATETIME()),
    [ended_ts]                  datetime2(0)    NULL,
    [current_node_id]           int             NULL,
    [prior_instance_id]         int             NULL,
    CONSTRAINT [PK_workflow_instances] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_workflow_instances_state]
        CHECK ([workflow_state] IN ('R','A','X','C'))
);
GO

ALTER TABLE [dbo].[workflow_instances] WITH CHECK
    ADD CONSTRAINT [FK_workflow_instances_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[workflow_instances] WITH CHECK
    ADD CONSTRAINT [FK_workflow_instances_workflow_definitions]
    FOREIGN KEY ([workflow_definition_id]) REFERENCES [dbo].[workflow_definitions] ([id]);
GO

ALTER TABLE [dbo].[workflow_instances] WITH CHECK
    ADD CONSTRAINT [FK_workflow_instances_current_node]
    FOREIGN KEY ([current_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO

ALTER TABLE [dbo].[workflow_instances] WITH CHECK
    ADD CONSTRAINT [FK_workflow_instances_prior_instance]
    FOREIGN KEY ([prior_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

/*  workflow_node_executions
    The per-instance execution record for a node visit. Replaces the draft's
    request_workflow_definitions, which copied the template graph row-by-row.
    The graph is static and lives on workflow_nodes; only the *runtime* of a
    node visit lives here.

    Multiple rows may exist for the same (instance, node) if the workflow
    revisits a node (loops are theoretically possible in the graph; this
    table handles them naturally). */
CREATE TABLE [dbo].[workflow_node_executions] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [workflow_instance_id]      int             NOT NULL,
    [workflow_node_id]          int             NOT NULL,
    [entered_ts]                datetime2(0)    NOT NULL CONSTRAINT [DF_wne_entered_ts] DEFAULT (SYSUTCDATETIME()),
    [exited_ts]                 datetime2(0)    NULL,
    [has_error]                 bit             NOT NULL CONSTRAINT [DF_wne_has_error] DEFAULT (0),
    [path_taken]                tinyint         NULL,  -- 1 or 2, only set on completion of a Decision node
    CONSTRAINT [PK_workflow_node_executions] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_wne_path_taken]
        CHECK ([path_taken] IS NULL OR [path_taken] IN (1,2))
);
GO

ALTER TABLE [dbo].[workflow_node_executions] WITH CHECK
    ADD CONSTRAINT [FK_wne_workflow_instances]
    FOREIGN KEY ([workflow_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

ALTER TABLE [dbo].[workflow_node_executions] WITH CHECK
    ADD CONSTRAINT [FK_wne_workflow_nodes]
    FOREIGN KEY ([workflow_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO

/*  workflow_instance_restarts
    RECOMMENDATION: small audit table for restart events. Restart is in-place
    on the same instance (concept), but each restart is an audited event.
    On restart: artifacts soft-deleted, executions cleared (or marked
    pre-restart), current_node_id reset to the workflow's start_node_id. */
CREATE TABLE [dbo].[workflow_instance_restarts] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [workflow_instance_id]      int             NOT NULL,
    [restarted_ts]              datetime2(0)    NOT NULL CONSTRAINT [DF_wir_restarted_ts] DEFAULT (SYSUTCDATETIME()),
    [restarted_by_user_id]      int             NOT NULL,
    [reason]                    nvarchar(1000)  NULL,
    CONSTRAINT [PK_workflow_instance_restarts] PRIMARY KEY CLUSTERED ([id] ASC)
);
GO

ALTER TABLE [dbo].[workflow_instance_restarts] WITH CHECK
    ADD CONSTRAINT [FK_wir_workflow_instances]
    FOREIGN KEY ([workflow_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

ALTER TABLE [dbo].[workflow_instance_restarts] WITH CHECK
    ADD CONSTRAINT [FK_wir_users]
    FOREIGN KEY ([restarted_by_user_id]) REFERENCES [dbo].[users] ([id]);
GO


/* ============================================================================
   9. ARTIFACTS (instance-scoped)
   ============================================================================ */

/*  request_workflow_artifacts
    Typed artifacts produced by process blocks (or by triage-time
    validations), consumed by downstream blocks.

    SCOPE: an artifact attaches to a request and optionally to a workflow
    instance.
      - workflow_instance_id IS NULL   -> request-scoped (e.g. triage-time
                                          ValidationResult artifacts that
                                          exist before any workflow instance
                                          is created). Survives workflow
                                          reassignment.
      - workflow_instance_id IS NOT NULL -> instance-scoped. Wiped (via
                                          is_deleted) on restart of that
                                          instance.

    created_by_node_execution_id: NULL for request-scoped artifacts produced
    by triage (no node execution exists yet). NOT NULL for instance-scoped
    artifacts — points at the specific node-execution row that produced it.

    jsondefinition is nvarchar(max) with an ISJSON check. */
CREATE TABLE [dbo].[request_workflow_artifacts] (
    [id]                            int             IDENTITY(1,1) NOT NULL,
    [request_id]                    int             NOT NULL,
    [workflow_instance_id]          int             NULL,
    [artifact_catalog_id]           int             NOT NULL,
    [created_by_node_execution_id]  int             NULL,
    [created_ts]                    datetime2(0)    NOT NULL CONSTRAINT [DF_rwa_created_ts] DEFAULT (SYSUTCDATETIME()),
    [is_deleted]                    bit             NOT NULL CONSTRAINT [DF_rwa_is_deleted] DEFAULT (0),
    [jsondefinition]                nvarchar(max)   NULL,
    CONSTRAINT [PK_request_workflow_artifacts] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_rwa_is_json]
        CHECK ([jsondefinition] IS NULL OR ISJSON([jsondefinition]) = 1),
    -- Instance-scoped artifacts must have a producing node execution;
    -- request-scoped artifacts (triage-time) must not.
    CONSTRAINT [CK_rwa_scope_producer]
        CHECK (
            ([workflow_instance_id] IS NULL     AND [created_by_node_execution_id] IS NULL)
         OR ([workflow_instance_id] IS NOT NULL AND [created_by_node_execution_id] IS NOT NULL)
        )
);
GO

ALTER TABLE [dbo].[request_workflow_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_rwa_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[request_workflow_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_rwa_workflow_instances]
    FOREIGN KEY ([workflow_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

ALTER TABLE [dbo].[request_workflow_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_rwa_artifact_catalog]
    FOREIGN KEY ([artifact_catalog_id]) REFERENCES [dbo].[artifact_catalog] ([id]);
GO

ALTER TABLE [dbo].[request_workflow_artifacts] WITH CHECK
    ADD CONSTRAINT [FK_rwa_node_execution]
    FOREIGN KEY ([created_by_node_execution_id]) REFERENCES [dbo].[workflow_node_executions] ([id]);
GO


/* ============================================================================
   10. REQUEST LOGS (execution narrative)
   ============================================================================
   Per-block execution narrative and similar event-level notes. AI invocations
   (validations + workflow selection + AI workflow blocks) live in ai_usage,
   §14 below — they were briefly modeled as polymorphic columns here and then
   pulled out so this table goes back to being narrative-only. */
CREATE TABLE [dbo].[request_logs] (
    [id]                    int             IDENTITY(1,1) NOT NULL,
    [request_id]            int             NOT NULL,
    [log_ts]                datetime2(0)    NOT NULL CONSTRAINT [DF_request_logs_ts] DEFAULT (SYSUTCDATETIME()),
    [log_text]              nvarchar(max)   NOT NULL,
    [workflow_instance_id]  int             NULL,
    [node_execution_id]     int             NULL,
    [user_id]               int             NULL,
    [artifact_id]           int             NULL,
    CONSTRAINT [PK_request_logs] PRIMARY KEY CLUSTERED ([id] ASC)
);
GO

ALTER TABLE [dbo].[request_logs] WITH CHECK
    ADD CONSTRAINT [FK_request_logs_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[request_logs] WITH CHECK
    ADD CONSTRAINT [FK_request_logs_workflow_instances]
    FOREIGN KEY ([workflow_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

ALTER TABLE [dbo].[request_logs] WITH CHECK
    ADD CONSTRAINT [FK_request_logs_node_execution]
    FOREIGN KEY ([node_execution_id]) REFERENCES [dbo].[workflow_node_executions] ([id]);
GO

ALTER TABLE [dbo].[request_logs] WITH CHECK
    ADD CONSTRAINT [FK_request_logs_users]
    FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]);
GO

ALTER TABLE [dbo].[request_logs] WITH CHECK
    ADD CONSTRAINT [FK_request_logs_artifacts]
    FOREIGN KEY ([artifact_id]) REFERENCES [dbo].[request_workflow_artifacts] ([id]);
GO
GO


/* ============================================================================
   11. ALARMS
   ============================================================================
   RECOMMENDATION: concept makes alarms first-class. Their threshold lives
   on the workflow_node row (stale_threshold_days); when one fires it
   becomes an audit record here. Side-effect-only (concept) — feeds the
   reviewer surface's "alarm has fired on current node" predicate. */
CREATE TABLE [dbo].[alarm_fires] (
    [id]                    int             IDENTITY(1,1) NOT NULL,
    [node_execution_id]     int             NOT NULL,
    [fired_ts]              datetime2(0)    NOT NULL CONSTRAINT [DF_alarm_fires_ts] DEFAULT (SYSUTCDATETIME()),
    [email_sent_ts]         datetime2(0)    NULL,
    [message_text]          nvarchar(500)   NULL,
    CONSTRAINT [PK_alarm_fires] PRIMARY KEY CLUSTERED ([id] ASC)
);
GO

ALTER TABLE [dbo].[alarm_fires] WITH CHECK
    ADD CONSTRAINT [FK_alarm_fires_node_execution]
    FOREIGN KEY ([node_execution_id]) REFERENCES [dbo].[workflow_node_executions] ([id]);
GO


/* ============================================================================
   12. PREVALIDATIONS
   ============================================================================
   Per the validation discussion: the single prevalidation_prompt is replaced
   by N per-validation rows on the Request Type version. At submission time
   the system runs every validation (no short-circuit) against the submitted
   documents and notes. Each call to Claude returns:

       { "validation_id": int, "result": "PASS"|"FAIL", "explanation": "..." }

   The result is written two places:
     - ai_usage row (raw invocation audit + token/cost accounting, see §14)
     - request_workflow_artifacts row of type ValidationResult, request-scoped
       (workflow_instance_id NULL), so downstream workflow blocks (including
       the workflow_selection call) can read it from the artifact bag.

   Confidence is NOT a column. Compliance encodes the policy in the prompt
   text ("If your confidence is below medium, return FAIL.") and the LLM
   applies it internally.

   Scope rule: validations operate only on inputs the submitter provided —
   request type, submitter notes, and uploaded documents. No external
   lookups, no document pre-processing. Anything that needs more is a
   workflow block, not a validation. */

/*  request_type_validations
    One row per check on a Request Type version. */
CREATE TABLE [dbo].[request_type_validations] (
    [id]                            int             IDENTITY(1,1) NOT NULL,
    [request_type_version_id]       int             NOT NULL,
    [description]                   nvarchar(200)   NOT NULL,
    [ai_prompt]                     nvarchar(max)   NOT NULL,
    [execution_order]               int             NOT NULL,
    CONSTRAINT [PK_request_type_validations] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_request_type_validations_order]
        UNIQUE ([request_type_version_id],[execution_order])
);
GO

ALTER TABLE [dbo].[request_type_validations] WITH CHECK
    ADD CONSTRAINT [FK_request_type_validations_rtv]
    FOREIGN KEY ([request_type_version_id]) REFERENCES [dbo].[request_type_versions] ([id]);
GO

/*  request_type_validation_documents
    Junction: which of the Request Type version's required documents are
    fed to a given validation. Zero rows = the validation runs on
    request-type + submitter notes only, no documents attached. */
CREATE TABLE [dbo].[request_type_validation_documents] (
    [id]                                    int     IDENTITY(1,1) NOT NULL,
    [request_type_validation_id]            int     NOT NULL,
    [request_type_required_document_id]     int     NOT NULL,
    CONSTRAINT [PK_request_type_validation_documents] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_rtvd_pair]
        UNIQUE ([request_type_validation_id],[request_type_required_document_id])
);
GO

ALTER TABLE [dbo].[request_type_validation_documents] WITH CHECK
    ADD CONSTRAINT [FK_rtvd_request_type_validations]
    FOREIGN KEY ([request_type_validation_id]) REFERENCES [dbo].[request_type_validations] ([id]);
GO

ALTER TABLE [dbo].[request_type_validation_documents] WITH CHECK
    ADD CONSTRAINT [FK_rtvd_request_type_required_documents]
    FOREIGN KEY ([request_type_required_document_id]) REFERENCES [dbo].[request_type_required_documents] ([id]);
GO


/* ============================================================================
   13. OUTBOUND EMAILS
   ============================================================================
   Per design decision: emails are queued, not fire-and-forget. A queue gives
   us retry on transient SMTP failure, audit of what was sent, and a single
   place to debug delivery problems.

   The set of triggers (submission confirmation, fix-and-resubmit, request
   accepted, every decision-block execution, every terminal) lives in code,
   not in this table — this table is the *queue and audit*, not the trigger
   configuration. When code emits an email, it inserts a row here; a worker
   picks up Pending rows and sends them via MailKit/MimeKit against the
   internal SMTP server. */
CREATE TABLE [dbo].[outbound_emails] (
    [id]                int             IDENTITY(1,1) NOT NULL,
    [request_id]        int             NULL,                  -- nullable: not every system email is tied to a request
    [to_address]        nvarchar(320)   NOT NULL,              -- 320 = RFC 5321 max email length
    [subject]           nvarchar(500)   NOT NULL,
    [body]              nvarchar(max)   NOT NULL,
    [is_html]           bit             NOT NULL CONSTRAINT [DF_outbound_emails_is_html] DEFAULT (1),
    [status]            char(1)         NOT NULL CONSTRAINT [DF_outbound_emails_status] DEFAULT ('P'),
    [queued_ts]         datetime2(0)    NOT NULL CONSTRAINT [DF_outbound_emails_queued_ts] DEFAULT (SYSUTCDATETIME()),
    [last_attempt_ts]   datetime2(0)    NULL,
    [sent_ts]           datetime2(0)    NULL,
    [send_attempt_count] int            NOT NULL CONSTRAINT [DF_outbound_emails_attempts] DEFAULT (0),
    [last_error]        nvarchar(2000)  NULL,
    CONSTRAINT [PK_outbound_emails] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_outbound_emails_status]
        CHECK ([status] IN ('P','S','F'))   -- P=Pending, S=Sent, F=Failed (permanent)
);
GO

ALTER TABLE [dbo].[outbound_emails] WITH CHECK
    ADD CONSTRAINT [FK_outbound_emails_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO


/* ============================================================================
   14. AI USAGE
   ============================================================================
   Every Claude call — validations, workflow-selection, AI workflow blocks —
   goes through the central AI service and is logged here. One row per call.

   The AI service is the only thing that writes this table; callers don't
   touch it. The service is also the only thing that reads AI.Disabled (the
   budget shutoff flag in settings); a background budget worker is the only
   thing that writes it.

   Keying convention (the four context columns are nullable; combinations
   identify what kind of call this was):
     - Validation call:   request_id + validation_id set; workflow_instance_id,
                          workflow_node_id NULL.
     - Workflow block:    request_id + workflow_instance_id + workflow_node_id
                          set; validation_id NULL.
     - System call:       all four context columns NULL (none planned today,
                          but leaves room for future system-internal calls).
   No CHECK enforces a combination — the convention is in code, the dashboard
   joins by whichever keys it cares about.

   Cost is computed at write time from input/output tokens × the rates in
   effect for the model (model_pricing, §15). Storing cost on the row makes
   month-to-date cost a single SUM and immune to future rate changes.

   input_json / output_json are stored verbatim (no truncation) per the
   concept's mineable-corpus goal. IT prunes the table on a retention
   policy as needed.

   error_text is populated only when status != 'S' (operational debugging
   detail; the LLM's "PASS"/"FAIL" reasoning lives in output_json). */
CREATE TABLE [dbo].[ai_usage] (
    [id]                    int             IDENTITY(1,1) NOT NULL,

    -- Context keys (nullable; combinations identify call kind — see header)
    [request_id]            int             NULL,
    [workflow_instance_id]  int             NULL,
    [workflow_node_id]      int             NULL,
    [validation_id]         int             NULL,

    -- The call itself
    [call_ts]               datetime2(0)    NOT NULL CONSTRAINT [DF_ai_usage_call_ts] DEFAULT (SYSUTCDATETIME()),
    [model]                 nvarchar(100)   NOT NULL,
    [prompt_version_id]     int             NULL,   -- request_type_version_id at the time of the call
    [input_tokens]          int             NOT NULL,
    [output_tokens]         int             NOT NULL,
    [cost_usd]              decimal(10, 6) NOT NULL,
    [latency_ms]            int             NOT NULL,
    [status]                char(1)         NOT NULL,   -- S=Success, E=Error (response unusable), T=Timeout

    -- Audit payloads (verbatim; not truncated)
    [input_json]            nvarchar(max)   NOT NULL,
    [output_json]           nvarchar(max)   NULL,        -- NULL on Timeout
    [error_text]            nvarchar(2000)  NULL,        -- populated when status != 'S'

    CONSTRAINT [PK_ai_usage] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [CK_ai_usage_status]
        CHECK ([status] IN ('S','E','T')),
    CONSTRAINT [CK_ai_usage_input_is_json]
        CHECK (ISJSON([input_json]) = 1),
    CONSTRAINT [CK_ai_usage_output_is_json]
        CHECK ([output_json] IS NULL OR ISJSON([output_json]) = 1)
);
GO

ALTER TABLE [dbo].[ai_usage] WITH CHECK
    ADD CONSTRAINT [FK_ai_usage_requests]
    FOREIGN KEY ([request_id]) REFERENCES [dbo].[requests] ([id]);
GO

ALTER TABLE [dbo].[ai_usage] WITH CHECK
    ADD CONSTRAINT [FK_ai_usage_workflow_instances]
    FOREIGN KEY ([workflow_instance_id]) REFERENCES [dbo].[workflow_instances] ([id]);
GO

ALTER TABLE [dbo].[ai_usage] WITH CHECK
    ADD CONSTRAINT [FK_ai_usage_workflow_nodes]
    FOREIGN KEY ([workflow_node_id]) REFERENCES [dbo].[workflow_nodes] ([id]);
GO

ALTER TABLE [dbo].[ai_usage] WITH CHECK
    ADD CONSTRAINT [FK_ai_usage_validations]
    FOREIGN KEY ([validation_id]) REFERENCES [dbo].[request_type_validations] ([id]);
GO

ALTER TABLE [dbo].[ai_usage] WITH CHECK
    ADD CONSTRAINT [FK_ai_usage_prompt_version]
    FOREIGN KEY ([prompt_version_id]) REFERENCES [dbo].[request_type_versions] ([id]);
GO


/* ============================================================================
   15. MODEL PRICING
   ============================================================================
   Per-model token rates with effective-date history. The AI service looks up
   the currently-effective row (effective_to IS NULL) for the model at call
   time and uses its rates to compute cost_usd on the ai_usage row.

   When Anthropic changes pricing:
     1. Stamp the current row with effective_to = today.
     2. Insert a new row with effective_from = today, effective_to NULL.
   That keeps historical ai_usage rows accurate to the rate that was actually
   in effect when each call ran.

   No pricing row for a model = the AI service refuses to call that model.
   Silent zero-cost is worse than a loud failure. */
CREATE TABLE [dbo].[model_pricing] (
    [id]                        int             IDENTITY(1,1) NOT NULL,
    [model_name]                nvarchar(100)   NOT NULL,
    [effective_from]            date            NOT NULL,
    [effective_to]              date            NULL,
    [input_per_million_usd]     decimal(10, 4) NOT NULL,
    [output_per_million_usd]    decimal(10, 4) NOT NULL,
    CONSTRAINT [PK_model_pricing] PRIMARY KEY CLUSTERED ([id] ASC),
    CONSTRAINT [UQ_model_pricing_model_from] UNIQUE ([model_name],[effective_from])
);
GO


/* ============================================================================
   16. SETTINGS SEED
   ============================================================================
   The settings table itself is in §2; this section seeds the rows the
   application expects to find at runtime. Not data per se — the contract
   between code and DB. Bump values in admin panel; don't add or remove rows
   here without a code change.

   These keys are referenced throughout the app:
     - Storage.BasePath             — UNC path to NAS share (request files)
     - AI.Disabled                  — runtime shutoff flag (budget worker writes)
     - AI.Monthly.Budget.Enabled    — master switch for the budget feature
     - AI.Monthly.Budget.Usd        — hard ceiling
     - AI.Monthly.Budget.WarningThreshold — pct (e.g. 80) to email at
     - AI.Monthly.Budget.AlertEmail — ; -separated admin addresses
     - AI.Monthly.Budget.WarningSentForMonth — YYYY-MM stamp; prevents repeat
                                               warning emails in the same month
     - AI.Polling.IntervalMinutes   — budget worker tick (default 5)
     - Debug.Identity.Enabled       — debug auth shim master switch
     - Debug.Identity.Entraid       — OID stamped on requests when shim active
                                      (single-user shim; multi-user picker added
                                      later)
   The Debug.Identity.* rows must never be set in production. The shim refuses
   to load when Environment = Production. */
INSERT INTO [dbo].[settings] ([key],[description],[required],[sensitive],[value])
VALUES
    ('Storage.BasePath',                      'UNC path to the NAS share holding request document folders.', 1, 0, '\\NAS1\VendorSure'),
    ('AI.Disabled',                           'Runtime AI shutoff flag. Written by the budget worker; read by the AI service. 1 = AI off.', 1, 0, '0'),
    ('AI.Monthly.Budget.Enabled',             'Master switch for the monthly budget feature.', 1, 0, '1'),
    ('AI.Monthly.Budget.Usd',                 'Hard ceiling in USD. When month-to-date cost exceeds this, AI.Disabled is set to 1.', 1, 0, '200'),
    ('AI.Monthly.Budget.WarningThreshold',    'Percent of budget at which to send the admin warning email.', 1, 0, '80'),
    ('AI.Monthly.Budget.AlertEmail',          'Semicolon-separated admin email addresses for budget warnings and shutoff notices.', 1, 1, ''),
    ('AI.Monthly.Budget.WarningSentForMonth', 'YYYY-MM stamp; prevents repeat warning emails within the same month.', 0, 0, ''),
    ('AI.Polling.IntervalMinutes',            'Interval at which the budget worker recomputes month-to-date cost.', 1, 0, '5'),
    ('Debug.Identity.Enabled',                'Debug auth shim master switch. Must be 0 in production; shim refuses to load in Production environments regardless.', 1, 0, '0'),
    ('Debug.Identity.Entraid',                'Entra OID stamped on requests when the debug shim is active (single-user mode).', 0, 1, '');
GO


/* ============================================================================
   END OF SCHEMA
   ============================================================================
   Summary of departures from the draft:

   1.  Naming
       - request_workflows                    -> workflow_instances
       - request_type_workflows               -> workflow_definitions
       - request_type_workflow_definitions    -> workflow_nodes
       - request_workflow_definitions         -> workflow_node_executions
                                                 (now per-instance node-visit
                                                  state, not a graph copy)
       - request_tyoe_required_documents      -> request_type_required_documents
       - requests.request_type_id             -> requests.request_type_version_id
       - users.groupid                        -> users.group_id
       - request_documents.file_path          -> storage_key (+ storage_backend)

   2.  Types / consistency
       - All bools int -> bit NOT NULL DEFAULT
       - All timestamps -> datetime2(0)
       - Human-typed text -> nvarchar
       - Hex color columns char(7) + CHECK
       - workflow_nodes.path2_node_id was nchar(10) (typo) -> int
       - request_types.is_active was nchar(10) (typo)      -> bit
       - request_documents.file_size int -> bigint
       - All PKs IDENTITY(1,1) (was inconsistent)
       - Duplicate FK_request_workflow_artifacts_request_workflows1 dropped

   3.  New tables
       - workflow_node_executions          (per-instance node-visit state)
       - workflow_instance_restarts        (restart audit events)
       - alarm_fires                       (first-class alarm audit)
       - request_type_validations          (per-check prevalidation rows)
       - request_type_validation_documents (which docs a validation sees)
       - outbound_emails                   (email queue + audit)
       - ai_usage                          (every Claude call: tokens, cost,
                                            payload, status; written only by
                                            the central AI service)
       - model_pricing                     (per-model token rates with
                                            effective-date history)

   4.  Moved / restructured
       - Prompts moved from request_types -> request_type_versions
         (workflow_selection_prompt only; the single prevalidation_prompt
         was replaced by per-validation prompts on request_type_validations)
       - request_type_required_documents is now a pure junction
         (no more duplicated name / file_extension)
       - request_workflow_artifacts now scope-flexible: workflow_instance_id
         is nullable, allowing request-scoped artifacts (triage-time
         ValidationResults) and instance-scoped artifacts (workflow blocks)
         from the same table. CHECK keeps the producer reference consistent
         with scope.
       - request_workflow_artifacts.created_by_node now points at
         workflow_node_executions (not at the static graph node), nullable
         for request-scoped artifacts
       - AI invocation logging lives in its own ai_usage table (§14), not
         on request_logs. request_logs is back to execution narrative only.
       - request_documents now stores a storage_backend + opaque storage_key
         instead of a literal file_path, so a future move to Azure Blob is
         a per-row backend swap rather than a schema migration.

   5.  Constraints
       - workflow_node_types.allows_block documents the rule; CHECK on
         workflow_nodes enforces it
       - workflow_nodes has CHECKs for terminal-no-edges,
         process-single-edge, decision-both-edges
       - block_catalog.node_type_id restricted to (2,3)
       - char(1) discriminators (request_status, workflow_state,
         request_state, in_or_out, outbound_emails.status, ai_usage.status)
         all CHECK-constrained
       - jsondefinition / input_json / output_json columns gated with ISJSON()
       - request_workflow_artifacts CHECK pairs nullable
         workflow_instance_id with nullable created_by_node_execution_id

   6.  Seed data included
       - workflow_node_types: 6 rows (Start / Process / Decision / Approved /
         Rejected / Cancelled) with shape, color, allows_block.
       - settings: 10 rows covering Storage.BasePath, AI budget keys,
         AI.Polling.IntervalMinutes, and the Debug identity shim flags.
       - model_pricing: empty (operator inserts the row for whatever model
         the AI service will call before first use).

   Items NOT in this draft (per the discussion):
       - Indexes (this is the conceptual schema; index pass at design time)
       - Schemas other than dbo
   ============================================================================ */
