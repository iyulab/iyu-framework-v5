import { createRoot } from 'react-dom/client';
import '@iyulab/house-style'; // the iyulab theme for the chat components; the page's own rules below are unlayered, so they win
import './styles.css';
import { App } from './App.jsx';
import { endpointsFor, makeApi, pageConfig } from './host-api.js';

// The page's way to the host: the same origin's fetch (the browser sends whatever sign-in the host uses), the routes where the server says.
const config = pageConfig();
const endpoints = endpointsFor(config.prefix);
const fetcher = (url, init) => fetch(url, init);

createRoot(document.getElementById('root')).render(<App config={config} endpoints={endpoints} fetcher={fetcher} api={makeApi(fetcher, endpoints)} />);

// Tooltips: anything with data-tip.
const tip = document.getElementById('tip');
document.addEventListener('mousemove', event => {
  const target = event.target.closest?.('[data-tip]');
  if (!target || !target.dataset.tip) { tip.style.display = 'none'; return; }
  tip.textContent = target.dataset.tip; // text only: a tip may carry catalog or host strings
  tip.style.display = 'block';
  tip.style.left = `${Math.min(event.clientX + 14, innerWidth - 200)}px`;
  tip.style.top = `${event.clientY + 14}px`;
});
