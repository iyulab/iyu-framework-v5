import { useEffect, useRef } from 'react';
import { UMarkedBlock, UMessage, UPrompt, UTextBlock } from '@iyulab/chat-components/react';
import { setAllowedImagePrefixes } from '@iyulab/chat-components';

// What the model writes is shown as the chat components show it: markdown, with the components' own rules for links and images.
// A reply may quote data the host holds, so a picture may come from this host and from nowhere else.
setAllowedImagePrefixes([`${location.origin}/`]);

/** What the opening message offers: prepared messages, pressed like any typed one. The assistant reads the person's own data, so these name none of it. */
export const STARTERS = ['What can I show from my data?', 'Suggest a first board from my data'];

/** The chips after a turn that failed in a way the person can answer with one press; a turn that went well offers none. */
export function chipsAfter(result) {
  const failure = result.failure;
  if (!failure) return [];
  if (failure.kind === 'permission') return failure.alternatives.slice(0, 2).map(field => `Use ${field}`);
  if (failure.kind === 'rejected') {
    const near = /Did you mean (.+?)\?/.exec(failure.suggestion)?.[1];
    return [...(near ? [`Use ${near}`] : []), 'Cancel'];
  }
  if (failure.kind === 'conflict') return [`Reload r${failure.serverRevision} and retry`];
  return [];
}

function Step({ step }) {
  const fail = step.state === 'error' || step.outcome === 'rejected';
  const cls = step.state === 'running' ? 'run' : step.state === 'pending' ? 'wait' : fail ? 'fail' : 'done';
  const icon = step.state === 'running' ? <span className="spin" /> : step.state === 'pending' ? '○' : fail ? '✕' : '✓';
  return (
    <div className={`step ${cls}`} data-step={step.kind} data-state={step.state} data-outcome={step.outcome ?? ''}>
      <span className="ic">{icon}</span>
      <div>
        <b>{step.label}</b>
        {step.detail ? <span style={{ color: 'var(--muted)' }}> — {step.detail}</span> : null}
        {step.raw && (step.state === 'done' || step.state === 'error') ? (
          <details>
            <summary>Show the call</summary>
            <pre className="mono" data-testid="raw-call">{step.raw}</pre>
          </details>
        ) : null}
      </div>
    </div>
  );
}

/** The assistant's panel. `canEdit` false: a person who may read the board but not change it — the panel says so and only answers. */
export function Chat({ thread, revision, busy, canEdit, onSend, onStop, onChip, pointing = [], onClearPointing }) {
  const ref = useRef(null);
  const prompt = useRef(null);
  useEffect(() => {
    if (ref.current) ref.current.scrollTop = ref.current.scrollHeight;
  }, [thread]);
  const submit = () => {
    const value = prompt.current?.value ?? '';
    if (!value.trim() || busy) return;
    prompt.current.value = '';
    onSend(value);
  };
  return (
    <aside className="chat" data-testid="chat">
      <div className="chead">
        <span className="tt">Assistant</span>
        <span className="pill hide-s" data-tip="Model calls go through your server; keys never reach the browser">via your server</span>
        <span className="grow" />
        <span className="pill">{revision ? `Draft r${revision}` : 'No revisions yet'}</span>
      </div>
      <div className="msgs" id="msgs" ref={ref}>
        {thread.map((m, i) => {
          if (m.role === 'u')
            return (
              <UMessage key={i} data-role="user" author="user" position="right" variant="bubble">
                <UTextBlock value={m.text} />
                {m.on?.length ? <span className="on" data-testid="user-on">on {m.on.map(o => o.title ?? o.id).join(', ')}</span> : null}
              </UMessage>
            );
          const last = i === thread.length - 1;
          return (
            <div key={i} className="m a" data-role="assistant">
              <div className="av">✦</div>
              <UMessage author="assistant" position="left" loading={busy && last && !m.text}>
                {m.steps?.length ? <div className="steps">{m.steps.map(step => <Step key={step.id} step={step} />)}</div> : null}
                {m.answer && m.text ? <span className="ans" data-testid="answer-only">Answer · no change to the board</span> : null}
                {m.text ? <UMarkedBlock data-testid="reply" value={m.text} /> : null}
                {last && m.chips?.length ? (
                  <div className="chips">{m.chips.map(chip => <button key={chip} className="chip" data-chip={chip} disabled={busy} onClick={() => onChip(chip)}>{chip}</button>)}</div>
                ) : null}
              </UMessage>
            </div>
          );
        })}
      </div>
      <div className="compose">
        <div className="ctx"><span>📌</span>{canEdit ? 'The open board is sent with every message' : 'Read only — answers only, no edits'}<span className="grow" /><span data-testid="ctx-rev">{revision ? `r${revision}` : ''}</span></div>
        {pointing.length ? (
          <div className="pointing" data-testid="selection-chip"><span>Pointing at</span><b>{pointing.map(p => p.title ?? p.id).join(', ')}</b><button aria-label="Clear selection" data-act="clear-selection" onClick={onClearPointing}>×</button></div>
        ) : null}
        <UPrompt ref={prompt} id="inp" loading={busy} minRows={1} maxRows={6} aria-label="Message the assistant"
          placeholder={!canEdit ? 'Ask a question about this board…' : pointing.length ? 'Say what to change about this…' : 'Ask for a chart, a filter, a change… or just ask a question'} onSend={submit} onStop={onStop} />
      </div>
    </aside>
  );
}
