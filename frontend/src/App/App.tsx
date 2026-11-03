import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';

import Activity from 'Activity/Activity';
import Queue from 'Approvals/Queue';
import Page from 'Components/Page/Page';
import Decisions from 'Decisions/Decisions';
import Status from 'System/Status';

import queryClient from './queryClient';

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Page>
          <Routes>
            <Route path="/" element={<Decisions />} />
            <Route path="/queue" element={<Queue />} />
            <Route path="/system/status" element={<Status />} />
            <Route path="/system/activity" element={<Activity />} />
          </Routes>
        </Page>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
