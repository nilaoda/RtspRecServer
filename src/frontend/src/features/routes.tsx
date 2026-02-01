import { Route, Routes } from 'react-router-dom'
import TasksPage from './tasks/TasksPage'
import ChannelsPage from './channels/ChannelsPage'
import RecordingsPage from './recordings/RecordingsPage'
import SettingsPage from './settings/SettingsPage'

const AppRoutes = () => {
  return (
    <Routes>
      <Route path="/" element={<TasksPage />} />
      <Route path="/channels" element={<ChannelsPage />} />
      <Route path="/recordings" element={<RecordingsPage />} />
      <Route path="/settings" element={<SettingsPage />} />
    </Routes>
  )
}

export default AppRoutes
