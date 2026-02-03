import { useEpgContext } from '../context/EpgContext'

export const useEpgData = () => {
  return useEpgContext()
}
