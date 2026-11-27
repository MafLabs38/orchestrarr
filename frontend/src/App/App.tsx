import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter, Route, Routes } from 'react-router-dom';

import Account from 'Account/Account';
import Accounts from 'Accounts/Accounts';
import Activity from 'Activity/Activity';
import Page from 'Components/Page/Page';
import Decisions from 'Decisions/Decisions';
import FlowPage from 'Flow/FlowPage';
import FlowHistoryPage from 'Flow/HistoryPage';
import QueuePage from 'Flow/QueuePage';
import LiveUpdates from 'Live/LiveUpdates';
import Missions from 'Missions/Missions';
import Options from 'Options/Options';
import RunDetail from 'Runs/RunDetail';
import Runs from 'Runs/Runs';
import Status from 'System/Status';

import queryClient from './queryClient';

export default function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <LiveUpdates />
      <BrowserRouter>
        <Page>
          <Routes>
            <Route path="/" element={<Decisions />} />
            <Route path="/history" element={<FlowHistoryPage />} />
            <Route path="/queue" element={<QueuePage />} />
            <Route path="/flow" element={<FlowPage />} />
            <Route path="/missions" element={<Missions />} />
            <Route path="/runs" element={<Runs />} />
            <Route path="/runs/:id" element={<RunDetail />} />
            <Route path="/system/status" element={<Status />} />
            <Route path="/system/activity" element={<Activity />} />
            <Route path="/system/accounts" element={<Accounts />} />
            <Route path="/system/options" element={<Options />} />
            <Route path="/account" element={<Account />} />
          </Routes>
        </Page>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
