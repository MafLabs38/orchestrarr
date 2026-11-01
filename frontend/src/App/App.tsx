import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';
import Decisions from 'Decisions/Decisions';
import Page from 'Components/Page/Page';
import Status from 'System/Status';
import queryClient from './queryClient';

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Page>
          <Routes>
            <Route path="/" element={<Decisions />} />
            <Route path="/system/status" element={<Status />} />
          </Routes>
        </Page>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
