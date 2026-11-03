import './Styles/themes.css';
import './Styles/variables.css';
import './Styles/global.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import App from 'App/App';

const root = document.getElementById('root');
if (!root) {
  throw new Error('The page has no #root element');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
