// The fixed header: identity, a breadcrumb (Library › Novel) with the
// Characters / Script switch once a novel is open, and the shared admin token.
export default function TopBar({ nav, status, creds }) {
  const inside = nav.titleId != null;
  return (
    <header className="topbar">
      <div className="brand">
        <button className="brand-mark as-link" onClick={nav.goHome} title="Library">
          ELVIN <em>IDE</em>
        </button>

        {inside ? (
          <>
            <span className="crumb-sep">›</span>
            <button className="crumb" onClick={nav.goHome}>Library</button>
            <span className="crumb-sep">›</span>
            <span className="crumb current">{nav.titleName}</span>
            <div className="view-switch" role="tablist">
              <button
                className={"vbtn" + (nav.section === "characters" ? " active" : "")}
                onClick={() => nav.setSection("characters")}
              >
                Characters
              </button>
              <button
                className={"vbtn" + (nav.section === "script" ? " active" : "")}
                onClick={() => nav.setSection("script")}
              >
                Script
              </button>
            </div>
            {nav.section === "script" && (
              <span className={"badge " + status.kind} title={status.title || ""}>{status.text}</span>
            )}
          </>
        ) : (
          <span className="brand-tag">library</span>
        )}
      </div>

      <div className="topbar-actions">
        <input
          className="field token"
          type="password"
          placeholder="admin token"
          title="Admin token (server -admin-token)"
          value={creds.token}
          onChange={(e) => creds.setToken(e.target.value)}
        />
      </div>
    </header>
  );
}
