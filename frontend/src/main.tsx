import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from 'App/App';
import './Styles/themes.css';
import './Styles/variables.css';
import './Styles/global.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>
);
