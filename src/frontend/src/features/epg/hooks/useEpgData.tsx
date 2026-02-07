import { useEpgContext } from '../context/useEpgContext'

export const useEpgData = () => {
  return useEpgContext()
}
